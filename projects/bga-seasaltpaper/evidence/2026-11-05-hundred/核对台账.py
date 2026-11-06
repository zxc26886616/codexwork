"""Audit a closed, authorized BGA batch. This tool has no game or queue controls."""
import argparse
from collections import Counter
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sys
from tempfile import NamedTemporaryFile

HERE = Path(__file__).resolve().parent
sys.path.insert(0,str(HERE/"批次监管"))
import build_upgrade
import allocated_seat
import cancel_review
import reboot_gap
BOOK = HERE / "百局台账.json"
HOST = "99769903"
SHANGHAI = timezone(timedelta(hours=8))
STRONG = {
    "receipt-unresolved-after-authoritative-read", "official-void-or-skipturn",
    "official-non-normal-end", "series-error-in-table", "platform-penalty",
    "platform-penalty-seat-revoked", "allocated-table-progress-unresolved", allocated_seat.CODE,
    "supervisor-fallback-started", "official-bound-table-conflict", "draw-receipt-handoff-required",
}
ACTION_TARGETS = {
    "打出选中的组合": ("playCards","playCards_button","打出选中的组合"),
    "喊停并结算本轮": ("playCards","immediateEndRound_button","立即结束本轮"),
    "宣布最后机会": ("playCards","endRound_button","最后机会"),
    "结束本回合": ("playCards","endTurn_button","结束回合"),
    "确认本轮得分": ("beforeEndRound","seen_button","我确认完了"),
}


def require(ok, why):
    if not ok:
        raise ValueError(why)


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def lines(path):
    if not path.is_file():
        return []
    return [json.loads(s) for s in path.read_text(encoding="utf-8-sig").splitlines() if s.strip()]


def store(path, data):
    with NamedTemporaryFile(dir=path.parent, delete=False, mode="w", encoding="utf8") as target:
        json.dump(data, target, ensure_ascii=False, indent=2)
        target.write("\n")
        target.flush()
        os.fsync(target.fileno())
    os.replace(target.name, path)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def when(iso):
    return datetime.fromisoformat(iso)


def receipt_matches(label, row):
    # 本地选牌与下一次服务端操作可能发生在同一秒。只有阶段与动作目标同时对应才关联回执。
    fields = (row.get("priorTarget") or "").split("|")
    phase_name = (row.get("priorPhase") or "").partition("/")[2].partition("/")[0]
    if len(fields) < 5 or fields[0] != phase_name:
        return False
    phase, target, text = fields[0], fields[3], fields[4]
    if label == "从牌库抽牌":
        return phase == "takeCards" and "deck" in target
    if label.startswith("选择保留"):
        return phase == "chooseCard" and target.startswith("ssp-card-")
    if label.startswith("把剩余的牌放入弃牌堆"):
        return phase == "putDiscardPile" and target in ("discard1","discard2") and label.endswith(target[-1])
    wanted = ACTION_TARGETS.get(label)
    return bool(wanted and phase == wanted[0] and (target == wanted[1] or (not target and text == wanted[2])))


def decisions(log, table, progress):
    answer = []
    rows = sorted(((when(e["at"]), e) for e in progress if e.get("Table") == f"BGA/{table}/座位/{HOST}"
                   and e.get("Id") and e.get("at")), key=lambda item:item[0])
    actions = []
    for line in log:
        fields = line.split("\u3000", 3)
        if len(fields) != 4 or fields[2] != table or not fields[3].startswith("自动 · "):
            continue
        label = fields[3].removeprefix("自动 · ")
        rescue = bool(re.match(r"^(?:保底行动[:：]|己方棋钟\s*\d+\s*秒[，,])", label))
        if label.startswith(("等待","正在")) or not (rescue or re.search(
                "选择保留|弃牌堆.*拿取|从弃牌堆取牌|打出|配对|发动|宣布|喊停|最后机会|亮出美人鱼|确认本轮得分|从牌库抽牌|结束本回合|放入弃牌堆", label)):
            continue
        at = datetime.strptime(fields[0], "%Y-%m-%d %H:%M:%S").replace(tzinfo=SHANGHAI)
        actions.append((at,label,rescue))
    possible = []
    for i, (at,label,rescue) in enumerate(actions):
        item = {"at":at.isoformat(), "decision":label}
        if rescue:
            item["rescue"] = True
        # 日志精度为秒。只使用在该秒之前已观测到的局面，避免把事后信息当作出牌依据。
        prior = next((e for stamp,e in reversed(rows) if stamp < at), None)
        if prior:
            item["lastObservedState"] = {k:prior.get(k) for k in ("at","phase","move")}
        # 表内点击可能在接口锁释放后才受理。用下一手的日志边界保留晚回执，不按固定秒数猜配。
        until = actions[i+1][0]+timedelta(seconds=1) if i+1 < len(actions) else None
        possible.append([e for stamp,e in rows if stamp >= at and (until is None or stamp < until)
                         and receipt_matches(label,e)])
        answer.append(item)
    occurrences = Counter(e["Id"] for group in possible for e in group)
    for item, group in zip(answer, possible):
        if len(group) == 1 and occurrences[group[0]["Id"]] == 1:
            e = group[0]
            item["matchingProgress"] = {k:e.get(k) for k in ("at","Id","priorPhase","phase","priorMove","move","priorTarget")}
    return answer


def review(directory, batch, pin, expected, *, evidence_directory=None, verify_public=False):
    if evidence_directory is None:
        evidence_directory=directory
    product = read(directory/(batch+".json"))
    status = read(directory/(batch+".status.json"))
    require(product["id"] == status["batchId"] == batch and product["planned"] == status["planned"] == expected,
            "批次或局数与票据不符")
    require(product["site"]=="BGA" and product["realtime"] is True and product["hostId"]==HOST,
            "批次站点、类型或账号不符")
    events = lines(directory/"监督.jsonl")
    ends = lines(directory/(batch+".ends.jsonl"))
    incidents = lines(directory/(batch+".incidents.jsonl"))
    fallbacks = lines(directory/(batch+".fallback.jsonl"))
    log = (directory/(batch+".log")).read_text(encoding="utf-8-sig").splitlines()
    exits = [e for e in events if e["kind"]=="worker-exit"]
    starts = [e for e in events if e["kind"]=="worker"]
    require(len(exits)==len(starts)==1 and exits[0]["pid"]==starts[0]["pid"],"原进程单次启动及退出证据不足")
    gap=None
    archived_exit=read(directory/"退出.json") if (directory/"退出.json").is_file() else {}
    if (exits[0].get("recovered") or exits[0].get("exitCode") is None or starts[0]["pid"]!=status["processId"]
        or archived_exit.get("recovered") or ("exitCode" in archived_exit and archived_exit["exitCode"] is None)):
        gap=reboot_gap.confirm(directory,batch,pin,Path.read_bytes)
    joins = [e for e in events if e.get("kind")=="observation" and "即时制已入队" in e.get("message","")]
    require(len(joins)<=expected, "入队次数超过放行额度")
    require(all(e.get("batch")==batch and e.get("site")=="BGA" for e in [*ends,*incidents,*fallbacks]), "混入其他批次证据")
    tables = dict.fromkeys([*(r["table"] for r in product["rounds"]), *(e["table"] for e in ends)])
    for e in [*events,*incidents,*fallbacks]:
        if re.fullmatch(r"[1-9]\d{0,17}",str(e.get("table",""))):
            tables[str(e["table"])] = None
        bound=re.search(r"已绑定匹配桌 (\d+)",e.get("message",""))
        if bound: tables[bound[1]]=None
    if product.get("currentTable"): tables[product["currentTable"]]=None
    require(len(tables)<=expected and len(product["rounds"])==product["completed"], "桌数或结算计数不符")

    normal, excluded = [], []
    for t in tables:
        require(re.fullmatch(r"[1-9]\d{0,17}",t), "无效桌号")
        final = [e for e in ends if e["table"]==t]
        rounds = [r for r in product["rounds"] if r["table"]==t]
        related = [e for e in incidents if e.get("table")==t]
        progress = lines(directory/f"BGA-{t}-{HOST}.progress.jsonl")
        if gap and t!=gap["table"]:
            progress=[r for r in progress if when(gap["originalStartedAt"])<=when(r["at"])<when(gap["bootAt"])]
        detail = {"batch":batch,"pin":pin,"table":t,"url":f"https://boardgamearena.com/table?table={t}",
                  "end":final, "incidents":related, "decisions":decisions(log,t,progress)}
        reasons = [e["code"] for e in related if e["code"] in STRONG]
        if gap and t==gap["table"]:
            reasons.append(reboot_gap.REASON)
            detail["rebootGap"]=gap
        elif gap and (len(final)!=1 or not when(gap["originalStartedAt"])<=when(final[0]["at"])<when(gap["bootAt"])):
            reasons.append("缺少重启前已收录的同桌官方终局")
        reasons += [e["code"] for e in fallbacks if e["table"]==t]
        if any(item.get("rescue") for item in detail["decisions"]):
            reasons.append("桌内触发合法保底或时钟紧急行动")
        for e in events:
            if e.get("table")==t and e.get("kind") in ("stop-request","handoff","takeover"):
                reasons.append(e["kind"])
        if len(final)!=1 or final[0].get("status") not in ("finished","archive") or final[0].get("cancelled")!="0" or final[0].get("endgame_reason")!="normal_end":
            reasons.append("未核实官方正常终局")
        record = directory/f"BGA-{t}.json"
        seats=[]
        if len(rounds)==1 and record.is_file():
            game=read(record)["match"]
            seats=[rounds[0]["host"],rounds[0]["guest"]]
            require(game["site"]=="BGA" and game["table"]==t and len(game["players"])==2
                    and {p["id"] for p in game["players"]}=={p["id"] for p in seats}
                    and seats[0]["id"]==HOST and seats[1]["id"]!=HOST
                    and sorted(seats,key=lambda p:p["id"])==sorted(game["players"],key=lambda p:p["id"])
                    and all(type(p["score"]) is int and p["rank"] in (1,2) for p in seats)
                    and min(p["rank"] for p in seats)==1, "官方比分或席位冲突："+t)
        else:
            reasons.append("缺少同桌批次与官方排名对照")
        rescue=[e for e in fallbacks if e.get("code")=="supervisor-fallback-ended" and e.get("table")==t and
                e.get("self")==HOST and e.get("details",{}).get("Table")==t]
        if rescue:
            data=rescue[-1]["details"]
            detail["fallbackOfficial"]={k:data.get(k) for k in ("OfficialStatus","Cancelled","EndReason","Scores")}
            parsed=[{k.lower():v for k,v in row.items()} for row in (data.get("Scores") or [])]
            if not seats and len(parsed)==2 and {p.get("id") for p in parsed}=={HOST,rescue[-1]["opponent"]}:
                seats=sorted(parsed,key=lambda s:s["id"]!=HOST)
        detail["seats"]=seats
        authoritative = [p for p in progress if p.get("Table")==f"BGA/{t}/座位/{HOST}" and seats and
                         p.get("Opponent")==seats[1]["id"] and p.get("Id") and
                         (p.get("boardChanged") is True or (isinstance(p.get("priorMove"),int) and
                          isinstance(p.get("move"),int) and p["move"]>p["priorMove"]))]
        if not authoritative or not final or min(final[0].get("confirmed",0),final[0].get("submitted",0))<1:
            reasons.append("缺少己方动作后的同桌服务端进展")
        if not detail["decisions"]: reasons.append("缺少自动关键决策")
        if verify_public:
            from official_results import query
            proof=directory/f"BGA-{t}-官方复核.json"
            if proof.exists():
                official=read(proof)
            else:
                try:
                    official=query(t)
                except (ValueError,OSError,TypeError,KeyError) as error:
                    if not reasons: raise RuntimeError("须只读补核原桌官方结果："+t) from error
                    detail["publicReadError"]=type(error).__name__
                    official=None
                if official and official["officialStatus"] in ("finished","archive"): store(proof,official)
            if official:
                require(official["table"]==t,"公开终局桌号错误")
                detail["publicResult"]=official
                if not reasons:
                    require(official["officialStatus"] in ("finished","archive") and official["cancelled"]=="0"
                            and official["endReason"]=="normal_end", "批次与公开终局原因冲突："+t)
                    def rankings(players): return sorted((p["id"],p["rank"],p["score"]) for p in players)
                    require(rankings(official["players"])==rankings(seats),"批次与公开排名不一致："+t)
                    require(all(str(p["isAi"]).lower() in ("0","false") for p in official["players"]),
                            "官方回包未证实两席均为普通玩家："+t)
        detail["confirmedProgressRows"]=len(authoritative)
        detail["exclusionReasons"]=list(dict.fromkeys(reasons))
        summary={k:detail[k] for k in ("table","batch","pin","url","seats","exclusionReasons")}
        if seats:
            summary["outcome"]="并列第一" if seats[0]["rank"]==seats[1]["rank"] else ("胜" if seats[0]["rank"]==1 else "负")
        target=evidence_directory/f"桌-{t}.json"
        summary["evidence"]=str(target.relative_to(HERE))
        store(target,detail)
        (excluded if reasons else normal).append(summary)
    stopfile=directory/(batch+".stop-queue")
    quota_only=cancel_review.quota_only(directory,batch,expected,len(joins),events,incidents,fallbacks)
    clean=(gap is None and exits[0].get("exitCode")==0 and status["status"]=="completed" and product["completed"]==expected
           and len(normal)==len(joins)==expected and not excluded and not product.get("currentTable")
           and not product.get("offerTable") and not product.get("queuePending")
           and not (directory/"熔断.json").exists() and (not stopfile.exists() or quota_only)
           and not any(e.get("kind") in ("stop-request","handoff","takeover") for e in events))
    return normal,excluded,clean


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("batch",help="init 或已退出的已授权批次 ID")
    args=p.parse_args()
    if args.batch=="init":
        if not BOOK.exists():
            initial=dict(target=100,total=0,assessedBatch=None,normalTables=[],excludedTables=[],
                         startedAt=datetime.now(timezone.utc).isoformat(),batches=[])
            with BOOK.open("x",encoding="utf8") as f: json.dump(initial,f,ensure_ascii=False,indent=2)
        print("当前累计",read(BOOK)["total"]); return
    batch=args.batch
    require(re.fullmatch(r"[0-9a-f]{32}",batch),"无效批次号")
    book=read(BOOK)
    if book["assessedBatch"]==batch:
        print("已核对",batch,book["total"]); return
    directory=HERE/"批次监管/批次"/batch
    ticket=read(directory/"票据.json")
    allow=read(directory/"E放行.json")
    require(ticket["batch"]==allow["batch"]==batch and ticket["pin"]==allow["pin"],
            "缺失本批及本包放行证据")
    if ticket["pin"] != build_upgrade.OLD_PIN:
        ticket_store=HERE/"批次监管"/"批次"
        first=ticket["upgradeBatch"]
        proof=build_upgrade.proof(HERE.parents[1],HERE,ticket_store,first,Path.read_bytes)
        first_ticket=read(ticket_store/first/"票据.json")
        ruling,mark=build_upgrade.record(ticket_store,first,Path.read_bytes)
        require(first_ticket["previous"]==ruling["previous"] and first_ticket["pin"]==ticket["pin"]==ruling["pin"]
                and first_ticket["batch"]==first_ticket["upgradeBatch"]==first
                and ticket["upgradeReviewSha256"]==first_ticket["upgradeReviewSha256"]==proof==mark,
                "独立核账无法核验本批的固定包换版依据")
    else:
        gate=read(HERE/"固定包验收.json")
        require(ticket["pin"]==gate["pin"]==build_upgrade.OLD_PIN and gate["fullP0Passed"] is True,
                "缺失历史固定包验收")
    if "cancelReviewSha256" in ticket:
        require(re.fullmatch(r"[0-9a-f]{32}",ticket["previous"]) and ticket["pin"]!=build_upgrade.OLD_PIN,
                "撤桌恢复票前序身份不符")
        receipt,total=cancel_review.proof(HERE/"批次监管"/"批次"/ticket["previous"],
                                          HERE/"批次监管"/"撤桌复核"/batch,Path.read_bytes)
        require(receipt==ticket["cancelReviewSha256"] and total==ticket["baseline"],"撤桌恢复票与独立核账不符")
    require(ticket["baseline"]==book["total"]==len(book["normalTables"]) and
            1<=ticket["count"]<=min(3,100-book["total"]) and batch not in [b["batch"] for b in book["batches"]],
            "额度或台账前序不符")
    new,bad,clean=review(directory,batch,ticket["pin"],ticket["count"],verify_public=True)
    previous={t["table"] for t in book["normalTables"]+book["excludedTables"]}
    require(not any(t["table"] in previous for t in new+bad),"跨批桌号重复，停止")
    book["normalTables"].extend(new)
    book["excludedTables"].extend(bad)
    book.update(total=len(book["normalTables"]),assessedBatch=batch)
    book["batches"].append(dict(batch=batch,pin=ticket["pin"],clean=clean,planned=ticket["count"],
                                normal=[r["table"] for r in new],excluded=[r["table"] for r in bad]))
    store(BOOK,book)
    print(json.dumps({"batch":batch,"normal":len(new),"excluded":len(bad),"clean":clean,"total":book["total"]}))


if __name__=="__main__":
    main()

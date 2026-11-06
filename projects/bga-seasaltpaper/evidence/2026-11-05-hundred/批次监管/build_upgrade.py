"""验收固定产品换版凭据。只读旧批、E记录和独立离线证据。"""
from datetime import datetime, timezone
import json
import re
from pathlib import Path
import allocated_seat
import reboot_gap
from notice_recovery import archive_digest, digest

OLD_PIN = "B1F476915B14A90DC0DB236D37661BBBDD4830D4D9B9FDEBA24C27554488C464"
PIN = "E829EA06F2129877F588DB5C0C71F542E17FD8A0340E2AAE6E17883D8563B9F2"
CANDIDATE = "1.3.2-p0-1bf51dfa4d873e9f"
PREVIOUS = "b86b83dbf7b64785a1e6e071a89df383"
ARCHIVE = "51D108758A76AE24F57BD9390AC59BF3876C04FC2453A995635620E2D6BED640"
LEDGER = "B80A4728DE35D6BF310DA9B49933594AFBDE304CCC318F377FF40B4683952D98"
ORIGINAL_CASES = "588345BBDCDE243D297B960730750518CD0147E0D0B1BB6D5C52D9F21ABA6A85"
INCIDENT = "receipt-unresolved-after-authoritative-read"

def need(ok, why):
    if not ok:
        raise ValueError("固定包换版拒绝：" + why)

def record(store, batch, read):
    need(isinstance(batch,str) and re.fullmatch(r"[0-9a-f]{32}",batch), "换版首批号无效")
    raw=read(store.parent/"换版复核"/batch/"E换版.json")
    row=json.loads(raw)
    need(row["decision"]=="E_ALLOW_FIXED_UPGRADE" and row["batch"]==batch and
         re.fullmatch(r"[0-9a-f]{32}",row["previous"]) and row["previous"]!=batch and
         re.fullmatch(r"[0-9A-F]{64}",row["pin"]) and row["pin"]!=row["fromPin"] and
         re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,110}",row["candidateId"]),
         "换版须绑定前批、不同新 pin 和固定候选目录名")
    return row,digest(raw)

def product(root, row, read, *, review_only=False):
    folder=(root/"dist/candidates"/row["candidateId"]).resolve()
    need(folder.parent==(root/"dist/candidates").resolve(),"候选目录越界")
    try:
        raw=read(folder/"candidate.json")
    except FileNotFoundError:
        if not review_only or folder.exists():
            raise
        # 旧版程序清理后，历史票据仍须核对封存的清单。实际启动只认安装目录。
        raw=read(root/"evidence/2026-11-05-hundred/批次监管/换版清单"/(row["pin"]+".json"))
    manifest=json.loads(raw)
    need(digest(raw)==row["pin"] and manifest["id"]==row["candidateId"],"固定包目录和签发 pin 不一致")
    return folder

def approved_cases(root,campaign,row,pin,table,incident,read):
    # 同一份底层证据可能支撑多个用例。单次核验只读一次，下一命令重新核验。
    refs = {}
    def checked(ref):
        path = Path(ref["path"])
        need(not path.is_absolute(), "证据路径须相对项目根")
        path = (root/path).resolve()
        need(path.is_relative_to(root/"evidence"), "证据路径超出项目 evidence")
        if path not in refs:
            refs[path] = digest(read(path))
        need(refs[path] == ref["sha256"], "原始证据指纹不符：" + str(path))
        return path
    def report(ref):
        raw = read(checked(ref))
        need(digest(raw) == ref["sha256"], "验收报告在核验期间发生变化")
        return json.loads(raw)
    targeted = report(row["targeted"])
    need(targeted["pin"] == pin and targeted["originalTable"] == table and targeted["incident"] == incident
         and targeted["passed"] is True and isinstance(targeted["evidence"],list) and targeted["evidence"],
         "缺少本固定包针对原桌事故的定向通过证据")
    for ref in targeted["evidence"]:
        checked(ref)
    suite = report(row["regression"])
    template = read(campaign/"百局准入75项.json")
    need(digest(template) == ORIGINAL_CASES, "原75项清单改变")
    expected = {(r["group"],r["case"]) for r in json.loads(template)["checks"]}
    need(suite["pin"] == pin and suite["total"] == suite["passed"] == len(suite["checks"]) == len(expected) == 75
         and {(r["group"],r["case"]) for r in suite["checks"]} == expected, "75项缺失、重复或用了其他固定包")
    for r in suite["checks"]:
        need(r["passed"] is True, "存在未通过的原75项复验")
        checked(r["evidence"])

def incident_evidence(old, row, snap, closed, read):
    prev=old.name
    table,code=row["originalTable"],row["incident"]
    need(isinstance(table,str) and re.fullmatch(r"[1-9][0-9]{0,17}",table) and
         code in ("receipt-unresolved-after-authoritative-read","draw-receipt-handoff-required",
                  "supervisor-fallback-started","official-void-or-skipturn","series-error-in-table",
                  "platform-penalty-seat-revoked","official-bound-table-conflict","allocated-table-progress-unresolved",
                  allocated_seat.CODE),
         "该事故不具备原桌 P0 证据类型")
    get=lambda name:json.loads(read(old/name))
    original=get("票据.json")
    fused=get("熔断.json")
    detail=get("桌-"+table+".json")
    incidents=[json.loads(x) for x in read(old/(prev+".incidents.jsonl")).splitlines()]
    series=get(prev+".json")
    exits=get("退出.json")
    known_exit=type(exits["exitCode"]) is int and exits["exitCode"] in (0,1,4) and not exits.get("recovered")
    if exits.get("recovered") or exits["exitCode"] is None:
        gap=reboot_gap.confirm(old,prev,row["fromPin"],read)
        known_exit=(gap["table"]==table and detail.get("rebootGap")==gap
                    and reboot_gap.REASON in detail.get("exclusionReasons",[]))
    matching=[e for e in incidents if e["batch"]==prev and e["site"]=="BGA" and e["table"]==table and e["code"]==code]
    if code==allocated_seat.CODE:
        handoff=get("需接管.json")
        proof=allocated_seat.official_play(matching[0],prev,table,series["hostId"]) if len(matching)==1 else None
        entered=bool(proof and handoff["batch"]==prev and handoff["table"]==table and handoff["reason"]==code
                     and handoff["takeover"] is True and handoff.get("officialEvidence")==proof
                     and matching[0] in detail.get("incidents",[]) and code in detail.get("exclusionReasons",[]))
    else:
        entered=any(e["actions"]>0 or bool(detail.get("confirmedProgressRows")) for e in matching)
    need(original["batch"]==series["id"]==fused["batch"]==detail["batch"]==prev and
         original["pin"]==fused["pin"]==detail["pin"]==row["fromPin"] and fused["reason"]==code and
         detail["table"]==table and series["site"]=="BGA" and series["realtime"] is True and
         series["hostId"]=="99769903" and known_exit
         and (old/(prev+".stop-queue")).is_file() and entered,
         "未核到同桌、同旧包和已入局的实际 P0")
    need(not any(e.get("endgame_reason")=="didntstart_players_left" for e in detail.get("end",[])),
         "开局前取消不得通过 P0 换版")
    terminal=any(e.get("table")==table and e.get("batch")==prev and e.get("site")=="BGA"
                 and e.get("status") in ("finished","archive") for e in detail.get("end",[]))
    public=detail.get("publicResult") or {}
    fallback=detail.get("fallbackOfficial") or {}
    need(terminal or (public.get("table")==table and public.get("officialStatus") in ("finished","archive")
                      and public.get("endReason")!="didntstart_players_left")
         or fallback.get("OfficialStatus") in ("finished","archive"), "尚无原桌官方终局，不能重新入队")
    normal=[r["table"] for r in snap["normalTables"] if r["batch"]==prev]
    excluded=[r["table"] for r in snap["excludedTables"] if r["batch"]==prev]
    audit=[r for r in snap["batches"] if r["batch"]==prev]
    need(len(audit)==1 and audit[0]["pin"]==original["pin"] and audit[0]["clean"] is False
         and audit[0]["normal"]==normal and audit[0]["excluded"]==excluded and table in excluded and
         table not in normal and closed["accepted"]==len(normal) and original["baseline"]+len(normal)==snap["total"],
         "P0 排除与本批实际入账数量不符")
    return original

def proof(root, campaign, store, new_batch, read):
    head=None
    seen_batches=set()
    seen_pins=set()
    cursor=new_batch
    child=None
    child_book=None
    while True:
        need(cursor not in seen_batches,"换版记录形成回路")
        seen_batches.add(cursor)
        row,mark=record(store,cursor,read)
        if head is None: head=mark
        pin=row["pin"]
        need(pin not in seen_pins and pin!=OLD_PIN,"不可复用旧固定包")
        seen_pins.add(pin)
        product(root,row,read,review_only=True)
        old=store/row["previous"]
        folder=store.parent/"换版复核"/cursor
        saved=read(folder/"旧台账.json")
        snap=json.loads(saved)
        closed=json.loads(read(old/"结批.json"))
        need(archive_digest(old,read)==row["previousArchiveSha256"] and
             digest(saved)==closed["ledgerSha256"]==row["previousLedgerSha256"] and closed["fused"] is True and
             snap["assessedBatch"]==old.name and snap["target"]==100 and
             snap["total"]==len(snap["normalTables"])==closed["auditedTotal"] and 0<=snap["total"]<=97,
             "P0 归档或独立结批账不符，或不足一次首三")
        tables=[x["table"] for x in snap["normalTables"]+snap["excludedTables"]]
        need(len(tables)==len(set(tables)),"结批台账出现重复或既排除又计入的桌号")
        review=datetime.fromisoformat(row["reviewedAt"])
        need(review.utcoffset() is not None and datetime.fromisoformat(closed["at"])<=review<=datetime.now(timezone.utc),
             "E 签发时间异常")
        if child is not None:
            need(child["previousUpgradeSha256"]==mark and child["fromPin"]==pin and
                 review<=datetime.fromisoformat(child["reviewedAt"]),"相邻两次换版的产品或 SHA 链断开")
            for key in ("batches","normalTables","excludedTables"):
                need(child_book[key][:len(snap[key])]==snap[key],"后继账簿改写历史 "+key)
        if row["fromPin"]==OLD_PIN:
            need(old.name==PREVIOUS and row["previousArchiveSha256"]==ARCHIVE and
                 row["previousLedgerSha256"]==LEDGER and pin==PIN and row["candidateId"]==CANDIDATE,
                 "与已冻结的历史根换版不符")
            approved_cases(root,campaign,row,pin,"943447870",INCIDENT,read)
            break
        origin=incident_evidence(old,row,snap,closed,read)
        need(row["previousUpgradeBatch"]==origin["upgradeBatch"] and
             row["previousUpgradeSha256"]==origin["upgradeReviewSha256"],"未绑定事故旧票所属的上一次 E 换版")
        approved_cases(root,campaign,row,pin,row["originalTable"],row["incident"],read)
        child,child_book,cursor=row,snap,row["previousUpgradeBatch"]
    return head

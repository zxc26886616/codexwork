"""复核已经确认邀约、尚未接席即被平台撤销的桌。不会派发动作或修改旧档。"""
from datetime import datetime, timezone
import json
import re
from build_upgrade import OLD_PIN
from notice_recovery import ORIGIN, HOST, archive_digest, clean_lobby, digest, timeline, when

REASON = "didntstart_players_left"
INCIDENT = "official-non-normal-end"

def quota_signal(batch, count):
    return f"SeaSalt supervisor exhausted admission budget {batch} {count}\n".encode("ascii")

def quota_only(directory, batch, expected, join_count, events, incidents, fallbacks):
    path=directory/(batch+".stop-queue")
    caps=[e for e in events if e.get("kind")=="admission-cap"]
    return (path.is_file() and path.read_bytes()==quota_signal(batch,expected)
            and len(caps)==1 and caps[0].get("count")==expected==join_count
            and not (directory/"熔断.json").exists() and not fallbacks
            and not any(e.get("kind") in ("stop-request","handoff","takeover") for e in events)
            and not any(e.get("code") not in ("page-read-slow","page-read-returned") for e in incidents))

def need(ok, message):
    if not ok:
        raise ValueError("开局前撤桌拒绝：" + message)

def proof(old, folder, read, fresh=False):
    def obj(f): return json.loads(read(f))
    def lines(f): return [json.loads(r) for r in read(f).splitlines() if r.strip()]
    prefix=old/old.name
    raw=read(folder/"E撤桌.json")
    e=json.loads(raw)
    new=folder.name
    table=e["table"]
    pin=obj(old/"票据.json")["pin"]
    need(pin!=OLD_PIN and re.fullmatch(r"[0-9A-F]{64}",pin), "仅可同包承接已升级的产品")
    need(e["decision"]=="E_ACCEPT_PRESTART_CANCEL" and e["pin"]==pin and e["previous"]==old.name
         and e["batch"]==new and old.name!=new and re.fullmatch(r"[0-9a-f]{32}",new)
         and re.fullmatch(r"[1-9][0-9]{0,17}",table) and e["lobbyRequestedUrl"]==ORIGIN+"/lobby",
         "E 未绑定固定包、前批、新批及原桌")
    need(archive_digest(old,read)==e["previousArchiveSha256"], "前批原始记录已变化")
    ticket=obj(old/"票据.json")
    closed=obj(old/"结批.json")
    state=obj(prefix.with_suffix(".json"))
    status=obj(prefix.with_suffix(".status.json"))
    alerts=obj(old/"首次警报.json")
    fused=obj(old/"熔断.json")
    count=ticket["count"]
    need(ticket["batch"]==state["id"]==status["batchId"]==fused["batch"]==old.name and
         ticket["pin"]==fused["pin"]==pin and 1<=count<=3 and state["planned"]==status["planned"]==count
         and state["site"]=="BGA" and state["hostId"]==HOST and state["realtime"] is True
         and state["offerTable"]==table and state["currentTable"] is None and state["queuePending"] is True
         and re.fullmatch(r"[1-9][0-9]{0,17}",state["opponentId"]) and state["opponentId"]!=HOST,
         "原始进度须只保留未接席的已分配双人原桌")
    need(closed["fused"] is True and fused["reason"]==INCIDENT and (prefix.with_suffix(".stop-queue")).is_file()
         and obj(old/"退出.json")["exitCode"]==1 and status["status"]=="failed" and status["table"] is None
         and alerts["reason"]==INCIDENT and alerts["table"]==table and alerts["ownClock"] is None
         and alerts["takeover"] is False and alerts["officialEnded"] is True, "退出、停排或技术异常不符")
    need(not any((old/f).exists() for f in ("需接管.json","需核实原桌.json","监管异常.json"))
         and not any(prefix.with_suffix(s).exists() for s in (".fallback.jsonl",".score-ack-hold"))
         and not list(old.glob(f"BGA-{table}-{HOST}*")) and not (old/f"BGA-{table}.json").exists(),
         "撤桌已留有棋钟、回执、比赛记录或兜底/接管")
    rounds=[r["table"] for r in state["rounds"]]
    need(len(set(rounds))==len(rounds)==state["completed"] and table not in rounds
         and len(rounds)<count,"混入已接席牌桌或批次额度耗尽")
    ends=lines(prefix.with_suffix(".ends.jsonl"))
    need([r["table"] for r in ends]==rounds+[table] and all(r["batch"]==old.name and r["site"]=="BGA" for r in ends),
         "终局缺失、重复或来自其他批")
    need(all(r["status"] in ("finished","archive") and r["cancelled"]=="0" and r["endgame_reason"]=="normal_end"
             and min(r["confirmed"],r["submitted"])>0 for r in ends[:-1]),"此前已收录的桌不全是正常完赛")
    end=ends[-1]
    need(end["status"]=="archive" and end["cancelled"]=="0" and end["endgame_reason"]==REASON
         and type(end["submitted"]) is type(end["confirmed"]) is int and end["submitted"]==end["confirmed"]==0,
         "原产品未记录同桌零提交、零确认的开局前撤销")
    incidents=lines(prefix.with_suffix(".incidents.jsonl"))
    target=[r for r in incidents if r.get("table")==table]
    need(len(target)==1 and target[0]["code"]==INCIDENT and type(target[0]["actions"]) is int
         and target[0]["actions"]==0 and all(r["batch"]==old.name and r["site"]=="BGA" and
         (r in target or (r.get("table") in rounds and r["code"] in ("page-read-slow","page-read-returned")))
         for r in incidents), "另有 P0 或原桌已执行动作")
    trace=obj(old/f"桌-{table}.json")
    need(trace["batch"]==old.name and trace["pin"]==pin and trace["table"]==table
         and trace["end"]==[end] and trace["incidents"]==target and trace["decisions"]==[]
         and trace["seats"]==[] and trace["confirmedProgressRows"]==0, "独立桌账发现决策或游戏进展")
    events=lines(old/"监督.jsonl")
    budget=[r for r in events if r["kind"]=="admission-cap"]
    need(not budget or (len(budget)==1 and budget[0]["count"]==count==len(rounds)+1
         and read(prefix.with_suffix(".stop-queue"))==quota_signal(old.name,count)),"批次额度闩身份不符")
    need([r["kind"] for r in events if r["kind"] not in ("observation","admission-cap")]==["worker","stop-request","worker-exit"]
         and events[0]["pid"]==events[-1]["pid"]==status["processId"] and events[0]["batch"]==old.name
         and events[0]["pin"]==pin and events[-1]["exitCode"]==1
         and any(r["kind"]=="stop-request" and r["reason"]==INCIDENT and r["table"]==table
                 and r.get("ownClock") is None for r in events), "监管记录不符或已有其他停排")
    def admissions(messages):
        return [s for s in messages if "即时制已入队" in s]
    def binds(messages):
        return [m[1] for s in messages if (m:=re.search(r"已绑定匹配桌 ([1-9][0-9]{0,17})(?![0-9])",s))]
    notes=[r.split("　",3) for r in read(prefix.with_suffix(".log")).decode("utf-8-sig").splitlines()]
    need(notes and all(len(r)==4 for r in notes),"原日志缺失或损坏")
    msgs=[r[3] for r in notes]
    observations=[r["message"] for r in events if r["kind"]=="observation"]
    need(admissions(msgs)==admissions(observations) and len(admissions(msgs))==len(rounds)+1<=count
         and binds(msgs)==binds(observations)==rounds+[table], "入队/分桌缺失、重复或超额")
    offset=next(i for i,r in enumerate(notes) if binds([r[3]])==[table])
    last=notes[offset:]
    need(all(r[2]=="大厅" and "自动 ·" not in r[3] for r in last), "受邀桌已进入游戏或执行操作")
    confirm=[i for i,r in enumerate(last) if r[3]=="确认平台分配的双人桌。"]
    ack=[i for i,r in enumerate(last) if r[3]=="己方已确认，等待对手或平台开局。"]
    need(len(confirm)==len(ack)==1 and 0<confirm[0]<=ack[0]
         and any(f"正在核对已确认的匹配桌 {table}" in r[3] for r in last[ack[0]+1:]), "己方邀约确认缺少同桌时序")
    saved=read(folder/"旧台账.json")
    book=json.loads(saved)
    normal=[r["table"] for r in book["normalTables"] if r["batch"]==old.name]
    excluded=[r for r in book["excludedTables"] if r["batch"]==old.name]
    audits=[r for r in book["batches"] if r["batch"]==old.name]
    need(digest(saved)==closed["ledgerSha256"]==e["previousLedgerSha256"] and book["assessedBatch"]==old.name
         and normal==rounds and len(excluded)==1 and excluded[0]["table"]==table and excluded[0]["pin"]==pin
         and len(audits)==1 and audits[0]["pin"]==pin and audits[0]["clean"] is False and audits[0]["normal"]==normal
         and audits[0]["excluded"]==[table] and closed["accepted"]==len(rounds) and
         book["total"]==closed["auditedTotal"]==ticket["baseline"]+len(rounds), "结批累计或撤桌排除证据不符")
    if not any(r["pin"]==pin and r["clean"] is True and r["planned"]==3 and len(r["normal"])==3 for r in book["batches"]):
        need(100-book["total"]>=3,"未通过连续首三且不足三个剩余额度")
    official=read(folder/"官方撤桌.json")
    lobby=read(folder/"原账号大厅.jsonl")
    need(digest(official)==e["officialSha256"] and digest(lobby)==e["lobbySha256"],"独立采集指纹不符")
    result=json.loads(official)
    need(result["table"]==table and result["url"]==f"{ORIGIN}/table?table={table}"
         and result["officialStatus"]=="archive" and result["cancelled"]=="0" and result["endReason"]==REASON
         and result["players"]==[], "官方未明确核实同桌开局前离开或已存在比赛排名")
    info=result["tableInfo"]
    need(info["gamestart"] is None and str(info["min_player"])==str(info["max_player"])=="2"
         and info["current_player_nbr"] is None and info["current_present_player_nbr"] is None,
         "官方桌的开局时间、双人设置或在席人数不符")
    readout=timeline(lobby,["background-start","before","read-only-complete","closed"])
    clean_lobby(readout)
    reviewed=when(e["reviewedAt"])
    need(when(end["at"])<=when(result["retrievedAt"])<=reviewed and
         when(closed["at"])<=when(readout[0]["at"])<=when(readout[-1]["at"])<=reviewed
         and readout[0]["pid"]!=status["processId"], "独立核验早于原桌结算/结批")
    if fresh:
        now=datetime.now(timezone.utc)
        need(reviewed<=now and 0<=(now-when(readout[1]["at"])).total_seconds()<=90,"大厅独立核验过期")
    return digest(raw),book["total"]

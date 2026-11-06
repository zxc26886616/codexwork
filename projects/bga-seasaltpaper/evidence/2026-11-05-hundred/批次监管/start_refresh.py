"""只读核对已放行批次的启动前刷新。消费绑定 E 原件后，禁止切换凭证。"""
from datetime import datetime, timezone
import json
import re
from notice_recovery import digest, timeline, clean_lobby, when

def need(ok, message):
    if not ok:
        raise ValueError("临启预检拒绝：" + message)

def select(d, pin, read):
    allow_raw=read(d/"E放行.json")
    allow=json.loads(allow_raw)
    original_raw=read(d/"大厅预检.json")
    original=json.loads(original_raw)
    original_hash=digest(original_raw)
    ticket_hash=digest(read(d/"票据.json"))
    need(allow["batch"]==original["batch"]==d.name and allow["pin"]==original["pin"]==pin
         and allow["ticketSha256"]==ticket_hash and allow["preflightSha256"]==original_hash,
         "原始预检或放行与票据不符")
    original_source=(d/original["source"]).resolve()
    need(original_source.parent==d and digest(read(original_source))==original["sourceSHA256"],"原始预检来源已变")
    bound=dict(allowSha256=digest(allow_raw),preflightSha256=original_hash)
    refpath=d/"E临启复核.json"
    if not refpath.exists():
        return original,bound
    refraw=read(refpath)
    ref=json.loads(refraw)
    need(ref["decision"]=="E_REFRESH_PREFLIGHT_ONLY" and ref["batch"]==d.name and ref["pin"]==pin
         and ref["allowSha256"]==bound["allowSha256"] and ref["originalPreflightSha256"]==original_hash
         and ref["ticketSha256"]==ticket_hash and re.fullmatch(r"临启预检-[0-9a-f]{32}\.json",ref["preflightFile"]),
         "E 复核未绑定本次原授权与新预检")
    raw=read(d/ref["preflightFile"])
    need(digest(raw)==ref["preflightSha256"],"新预检文件指纹不符")
    pre=json.loads(raw)
    source=(d/pre["source"]).resolve()
    need(source.parent==d and source.suffix==".jsonl" and source!=original_source,"需要同批独立的新原始采集")
    data=read(source)
    need(digest(data)==pre["sourceSHA256"],"新原始采集指纹不符")
    rows=timeline(data,["background-start","before","read-only-complete","closed"])
    clean_lobby(rows)
    need(when(allow["at"]) <= when(rows[0]["at"]) and when(rows[1]["at"])==when(pre["checkedAt"])
         and when(rows[-1]["at"])<=when(ref["reviewedAt"])<=datetime.now(timezone.utc),
         "采集必须在原放行后完成，预检时间须取原始实读")
    bound.update(preflightSha256=ref["preflightSha256"],refreshSha256=digest(refraw))
    return pre,bound

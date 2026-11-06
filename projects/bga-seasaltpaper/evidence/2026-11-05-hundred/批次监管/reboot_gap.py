"""独立核验主机重启、同桌停排救场以及官方终局，只允许排除断档桌。"""
from datetime import datetime,timezone
import json
import os
from pathlib import Path
import re
from notice_recovery import digest, when, HOST, ORIGIN

REASON="主机重启造成原进程退出码未知；同桌救场暂停交接"

def need(ok, why):
    if not ok:
        raise ValueError("重启断档复核拒绝："+why)

def confirm(d,batch,pin,read):
    rescue="断电原桌救场/"
    approval=read(d/(rescue+"E重启复核.json"))
    e=json.loads(approval)
    t=e["table"]
    need(e["decision"]=="E_EXCLUDE_REBOOT_GAP" and e["batch"]==d.name==batch and e["pin"]==pin
         and re.fullmatch(r"[1-9][0-9]{0,17}",t) and e["originalExitCode"] is None
         and all(e[k] is True for k in ("lostOriginalTree","rescueExited","noNewQueue")),
         "E 未核实原树丢失、仅保原桌及救场退出")
    required={"票据.json","E放行.json","消费.json","发车意图.json","守护就绪.json","worker.json","监督.jsonl","退出.json","熔断.json",
              *(batch+x for x in (".json",".status.json",".incidents.jsonl",".log",".stop-queue")),f"BGA-{t}-官方复核.json",
              *(rescue+x for x in ("单次救场意图.json","救场进程.json","原进程末次状态.json","重启与收尾.json","官网席位只读.json"))}
    need(required<=e["files"].keys(),"缺少原始证据指纹")
    data={}
    for name,sha in e["files"].items():
        p=(d/name).resolve()
        need(p.is_relative_to(d.resolve()) and p.relative_to(d.resolve()).as_posix()==name,"证据路径越界")
        raw=read(p)
        need(digest(raw)==sha,"指纹不符："+name)
        data[name]=raw
    obj=lambda n:json.loads(data[n])
    lines=lambda n:[json.loads(s) for s in data[n].splitlines() if s.strip()]
    orig,supervisor,guard,new=(e[k] for k in ("originalPid","supervisorPid","sentinelPid","rescuePid"))
    need(all(type(p) is int and p>0 for p in (orig,supervisor,guard,new)) and len({orig,supervisor,guard,new})==4,"PID冲突")
    ticket,allow,consume=obj("票据.json"),obj("E放行.json"),obj("消费.json")
    worker,armed,sentinel=obj("worker.json"),obj("发车意图.json"),obj("守护就绪.json")
    observations=lines("监督.jsonl")
    ws=[x for x in observations if x["kind"]=="worker"]
    xe=[x for x in observations if x["kind"]=="worker-exit"]
    ex=obj("退出.json")
    need(ticket["batch"]==allow["batch"]==consume["batch"]==worker["batch"]==batch
         and ticket["pin"]==allow["pin"]==consume["pin"]==worker["pin"]==pin
         and allow["ticketSha256"]==digest(data["票据.json"]) and ticket["count"]==allow["count"]==consume["count"]
         and worker["pid"]==orig and armed["supervisor"]==sentinel["parent"]==supervisor and sentinel["pid"]==guard
         and len(ws)==len(xe)==1 and ws[0]["batch"]==batch and ws[0]["pin"]==pin and ws[0]["pid"]==xe[0]["pid"]==orig
         and ws[0]["exe"]==worker["exe"] and xe[0]["exitCode"] is ex["exitCode"] is None and
         xe[0]["recovered"] is ex["recovered"] is ex["requiresReview"] is True,"原始一次启动、退出缺口或消费不符")
    request=obj(rescue+"单次救场意图.json"); process=obj(rescue+"救场进程.json"); last=obj(rescue+"原进程末次状态.json")
    reboot=obj(rescue+"重启与收尾.json"); seats=obj(rescue+"官网席位只读.json")
    official=obj(f"BGA-{t}-官方复核.json")
    status=obj(batch+".status.json");series=obj(batch+".json");fuse=obj("熔断.json")
    boot=when(reboot["lastBootUpTime"])
    need(last["batchId"]==status["batchId"]==series["id"]==batch and last["processId"]==orig and last["status"]=="running"
         and last["table"]==status["table"]==series["currentTable"]==t and status["processId"]==new and status["status"]=="failed"
         and series["site"]=="BGA" and series["hostId"]==HOST and series["realtime"] is True
         and last["completed"]==series["completed"]==status["completed"]==len(series["rounds"])
         and last["planned"]==status["planned"]==series["planned"]==ticket["count"]
         and series["offerTable"] is None and series["queuePending"] is False
         and request["table"]==process["table"]==seats["table"]==official["table"]==t and request["reason"]=="host-reboot"
         and request["batch"]==batch and request["pin"]==pin and request["oldWorker"]==orig and request["oldSupervisor"]==supervisor
         and process["pid"]==reboot["rescuePid"]==new and process["exe"]==worker["exe"] and process["sha256"]==ws[0]["exeSHA256"],
         "重启前后不是原产品、同一批次或相同原桌")
    args=e["rescueArgs"]
    need(args==["--resume",batch] or (isinstance(args,list) and len(args)==4 and args[:3]==["--resume",batch,"--root"]
         and Path(args[3]).resolve()==(Path(os.environ["LOCALAPPDATA"])/"SeaSaltDesk").resolve()),"救场不限于此原批")
    need(when(worker["at"])<=when(last["updated"])<boot<when(request["at"])<=when(process["at"])<=when(status["updated"])
         <=when(seats["at"])<=when(official["retrievedAt"])==when(reboot["confirmedAt"])<=when(ex["at"])
         <=when(e["reviewedAt"])<=datetime.now(timezone.utc), "启动、重启、救场、终局或收尾时序不符")
    def logged(provider,event):
        return any(r["ProviderName"]==provider and r["Id"]==event and
                   abs((when(r["TimeCreated"])-boot).total_seconds())<=120 for r in reboot["events"])
    need(logged("Microsoft-Windows-Kernel-General",12) and logged("EventLog",6005),"系统启动与事件日志服务证据不全")
    opponent=series["opponentId"]
    need(re.fullmatch(r"[1-9][0-9]{0,17}",opponent) and opponent!=HOST and official["url"]==f"{ORIGIN}/table?table={t}"
         and official["officialStatus"] in ("finished","archive") and official["cancelled"] in ("0","1")
         and official["endReason"]==reboot["terminal"] and official["endReason"] not in (None,"","didntstart_players_left")
         and str(official["tableInfo"]["gamestart"]).isdecimal() and
         seats["status"]==seats["state"]["status"]==official["officialStatus"] and
         str(seats["state"]["gamestart"])==str(official["tableInfo"]["gamestart"])
         and len(official["players"])==len(seats["seats"])==2 and {r["id"] for r in official["players"]}==
         {r["idKey"] for r in seats["seats"]}=={HOST,opponent},"缺少原桌双方的独立官方已开局终局")
    inc=lines(batch+".incidents.jsonl")
    original=[r for r in inc if r["table"]==t and r["code"]==fuse["reason"] and when(r["at"])<boot]
    rescued=[r for r in inc if when(r["at"])>=boot]
    stop=data[batch+".stop-queue"].decode().strip()
    quota=f"SeaSalt supervisor exhausted admission budget {batch} {ticket['count']}"
    held_before=(any(r["kind"]=="admission-cap" and r["count"]==ticket["count"] and r["at"]<=boot.timestamp()
                     for r in observations) if stop==quota else when(stop)<=boot)
    need(fuse["batch"]==batch and fuse["pin"]==pin and when(fuse["at"])<boot and original and
         all(r["site"]=="BGA" and r["batch"]==batch for r in inc) and len(rescued)==1
         and rescued[0]["code"]=="series-error-in-table" and rescued[0]["table"]==t and rescued[0]["actions"]==0
         and when(rescued[0]["at"])>=when(process["at"])
         and any(r["kind"]=="stop-request" and r["table"]==t and r["at"]<=boot.timestamp() for r in observations)
         and held_before,"中断前缺少停排、原事故或救场期间有其他动作")
    chunks=[line.split("　",3) for line in data[batch+".log"].decode("utf-8-sig").splitlines()]
    starts=[i for i,r in enumerate(chunks) if len(r)==4 and r[3].startswith("即时制构建　座位 ")]
    need(len(starts)==2 and chunks[starts[0]][3]==chunks[starts[1]][3],"原产品与救场实载身份或次数不符")
    tail=chunks[starts[1]:]
    need(all(len(r)==4 and r[2]==t and not r[3].startswith("自动 ·") and not any(s in r[3] for s in
             ("即时制已入队","确认平台分配的双人桌","已绑定匹配桌","开始海盐折纸双人")) for r in tail)
         and "找回了中断的原桌" in status["message"] and "已暂停" in status["message"]
         and any("找回了中断的原桌" in r[3] and "已暂停" in r[3] for r in tail),"重接过程未限于原桌暂停交接")
    progress=d/f"BGA-{t}-{HOST}.progress.jsonl"
    if progress.is_file():
        need(all(when(json.loads(r)["at"])<boot for r in read(progress).splitlines() if r.strip()),
             "重启后出现新的原桌动作回执")
    return dict(batch=batch,table=t,originalPid=orig,originalExitCode=None,rescuePid=new,
                originalStartedAt=worker["at"],bootAt=reboot["lastBootUpTime"],
                terminal=official["endReason"],reviewSha256=digest(approval))

"""大厅预检唯一发现链。生产模式只处理已有未消费票据；隔离模式的读写和 WebView 都限定在临时根。"""
import argparse
import ctypes
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import uuid
from contextlib import contextmanager, nullcontext
from datetime import datetime, timezone
from pathlib import Path

HERE=Path(__file__).resolve().parent


class Refusal(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code=code


def require(condition, message, code=5):
    if not condition:
        raise Refusal(code,message)


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def clean_snapshot(rows, raw, extended):
    before=[r for r in rows if r["stage"]=="before"]
    done=[r for r in rows if r["stage"]=="read-only-complete"]
    require(len(before)==len(done)==1,"只读证据缺行："+str(raw))
    page=before[0].get("page",before[0].get("data"))
    result=done[0].get("data",done[0])
    user=page.get("user") or {}
    age=(datetime.now(timezone.utc)-datetime.fromisoformat(before[0]["at"])).total_seconds()
    require(0<=age<=90 and result.get("blockedNavigation")==0 and page.get("origin")=="https://boardgamearena.com" and
            page.get("path")=="/lobby" and user.get("id")=="99769903" and user.get("name")=="bke780","首源、账号、时间或导航不符："+str(raw))
    require(all(isinstance(page.get(k),list) for k in ("progress","queuedIds","overlays","noticeTables","noticeLines","headings","controls")),
            "只读页面字段不全："+str(raw))
    lobby_headings={"","帮助","贡献","导航","关注我们","你和朋友想同屏游玩吗？","查找游戏","我们给您推荐的游戏","你的游戏"}
    unknown_heads=[h for h in page["headings"] if str(h).strip() not in lobby_headings]
    require(not any(str(p.get("text") or "").strip() for p in page["progress"]) and not unknown_heads and
            not any(page[k] for k in ("queuedIds","overlays","noticeTables","noticeLines")),
            "存在在途桌、队列、提醒或未核实标题："+str(raw))
    module=page.get("lobby") or {}
    playable=any(c.get("id") in ("Xannaplay_1631","wannaplay_1631") and c.get("disabled") is False and
                 re.search("海盐折纸|Sea Salt",c.get("text",""),re.I) and re.search("开始游戏|Play now",c.get("text",""),re.I) for c in page["controls"])
    ready=module.get("visible") is True and {"rankingmode_simple","gamemode_realtime","opponentmode_automatic"}<=set(module.get("modes",[])) and playable
    if extended:
        require(ready and page.get("dialogs")==[] and page.get("noticeControls")==[] and page.get("credentials")==0 and
                result.get("noAction") is True and result.get("cleanSamples",0)>=3,
                "补充读未证实完整空闲大厅，或存在弹窗/凭据输入/提醒控件："+str(raw))
    return ready,before


@contextmanager
def inspecting(batch):
    # 持整个流程，但不持监管的发车锁；子进程会各自在发车锁内复核。
    dll=ctypes.WinDLL("kernel32",use_last_error=True)
    dll.CreateMutexW.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_wchar_p]
    dll.CreateMutexW.restype=ctypes.c_void_p
    dll.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_uint32]
    dll.ReleaseMutex.argtypes=[ctypes.c_void_p]
    dll.CloseHandle.argtypes=[ctypes.c_void_p]
    h=dll.CreateMutexW(None,False,"Local\\SeaSalt-preflight-"+batch)
    require(h,"无法取得本批预检互斥锁",6)
    owned=False
    try:
        owned=dll.WaitForSingleObject(h,0) in (0,128)
        require(owned,"本批已有进行中的预检，不并行处置",6)
        yield
    finally:
        if owned:dll.ReleaseMutex(h)
        dll.CloseHandle(h)


def main(args):
    require(re.fullmatch("[0-9a-f]{32}",args.batch),"批号无效")
    regulator=None
    steps=[]
    if args.fixture:
        root=args.fixture.resolve()
        require(root.parent==Path(tempfile.gettempdir()).resolve() and root.name.startswith("SeaSalt-notice-fixture-"),"隔离根不符")
        fixture=load(root/"场景.json")
        store=root/"evidence"/"2026-11-05-hundred"/"批次监管"/"批次"
        pin=fixture["pin"]
        steps=list(fixture["steps"])
    else:
        sys.path.insert(0,str(HERE.parent))
        import 监管 as regulator
        store=regulator.STORE
        receipt=regulator.get(regulator.batch_dir(args.batch)/"票据.json")
        if receipt.get("upgradeBatch"):
            regulator.select_product(receipt["upgradeBatch"],receipt["pin"],receipt["upgradeReviewSha256"])
        pin=regulator.PIN
    batch=store/args.batch
    ticket=batch/"票据.json"
    initial=digest(ticket)
    summary=batch/"大厅预检.json"
    lock=lambda:regulator.lock(store/"监管.lock") if regulator else nullcontext()

    def available():
        row=load(ticket)
        require(row["batch"]==args.batch and row["pin"]==pin and digest(ticket)==initial,"票据、当前版本或本次票据指纹已变化")
        forbidden=("E放行.json","消费.json","发车意图.json","worker.json","熔断.json","退出.json","结批.json")
        require(not any((batch/name).exists() for name in forbidden),"该票据已有放行、消费、熔断或收尾记录，拒绝复用")
        if regulator:
            chain,baseline=regulator.tickets()
            require(chain and chain[-1]["batch"]==args.batch and chain[-1]["baseline"]==baseline,"当前票据不是可处理的最新一张")
            require(not any(k.startswith("WEBVIEW2_") for k in os.environ),"正式 WebView 环境有外部覆盖")
            require(not regulator.held(regulator.DATA/"series-run.lock") and not list((regulator.DATA/"series").glob(args.batch+".*")),
                    "账号有在途对局或本票据已有正式资料")
        return row

    def child(mode, expected="-"):
        with lock():available()
        run=uuid.uuid4().hex
        prefix={"discover":"大厅只读-","fixed":"固定大厅只读-","strong":"补充大厅只读-"}.get(mode,"账号处置-")
        raw=batch/(prefix+run+".jsonl")
        if args.fixture:
            require(steps and steps[0]["mode"]==mode,"隔离夹具未预期本次调用")
            scenario=steps.pop(0)["scenario"]
            cmd=[str(args.exe),"--fixture-batch",scenario,str(root)]
        else:
            cmd=[str(args.exe),"--batch"]
        proc=subprocess.run([*cmd,args.batch,run,mode,expected],creationflags=subprocess.CREATE_NO_WINDOW,
                            stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
        require(raw.is_file(),"账号进程未生成本次原始证据，返回 "+str(proc.returncode),6)
        rows=[json.loads(line) for line in raw.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        if mode=="fixed" and not args.fixture:
            require(rows and rows[0]["stage"]=="background-start" and rows[-1]["stage"]=="closed" and
                    rows[0]["pid"]==rows[-1]["pid"] and rows[-1]["exitCode"]==proc.returncode==0,
                    "固定 ReadOnly.exe 未完整退出："+str(raw),6)
            return raw,rows,proc.returncode
        require(rows and rows[0]["stage"]=="start" and rows[-1]["stage"]=="closed" and
                rows[-1]["data"]["exitCode"]==proc.returncode and not rows[-1]["data"]["foregroundOwned"],
                "账号进程证据不完整："+str(raw),6)
        return raw,rows,proc.returncode

    def row_at(rows,stage):
        return [r for r in rows if r["stage"]==stage]

    def details(rows):
        parts=[]
        for r in rows:
            if r["stage"] in ("before","decision","error","navigation-held","personal-step","after","cooldown-recheck","cooldown-pending"):
                v=r["data"]
                for k in ("need","reason","hint","condition","deadline","message","path"):
                    value=v.get(k)
                    if value and str(value) not in parts:
                        parts.append(str(value))
        return "；".join(parts)[:850]

    with lock():
        available()
        # 较早成功的快照不能在本次新的发现失败后继续用于发车。保留其原始字节。
        if summary.exists():
            summary.replace(batch/("前次预检-"+uuid.uuid4().hex+".json"))
    evidence=[]
    handled=set()
    child_pids=set()
    fallback_taken=False
    for _ in range(9):
        raw,rows,code=child("discover")
        require(code==0,"发现进程拒绝："+details(rows)+"；原件 "+str(raw),code or 6)
        start=rows[0]["data"]["pid"]
        require(start not in child_pids,"发现未使用新进程",6)
        child_pids.add(start)
        before=row_at(rows,"before")
        require(len(before)==1,"缺少唯一发现快照："+str(raw),6)
        page=before[0]["data"]
        require(page.get("origin")=="https://boardgamearena.com" and page.get("hostId")=="99769903" and page.get("account")=="bke780",
                "正式站或账号未核实："+str(raw))
        kind=page["kind"]
        pending=row_at(rows,"cooldown-pending")
        if (not fallback_taken and not pending and kind=="loading" and page.get("path")=="/lobby" and page.get("controls")==[] and
            page.get("hint")=="账号已载入，等待大厅模块和游戏入口" and page.get("table") is None and page.get("eligible") is False):
            fallback_taken=True
            evidence.append({"file":raw.name,"sha256":digest(raw),"kind":"loading"})
            fixed,fr,fc=child("fixed")
            fixed_pid=fr[0].get("pid",fr[0].get("data",{}).get("pid"))
            require(fc==0 and fixed_pid not in child_pids,"固定只读器未在独立进程完成："+str(fixed),6)
            child_pids.add(fixed_pid)
            first_ready,_=clean_snapshot(fr,fixed,False)
            evidence.append({"file":fixed.name,"sha256":digest(fixed),"kind":"pinned-readonly","ready":bool(first_ready)})
            raw,rows,code=child("strong")
            require(code==0 and rows[0]["data"]["pid"] not in child_pids,"补充只读器未在独立进程完成："+str(raw),6)
            _,before=clean_snapshot(rows,raw,True)
            evidence.append({"file":raw.name,"sha256":digest(raw),"kind":"fresh-modal-aware-readonly"})
            kind="idle";page={"path":"/lobby","readySeaSalt":True}
        if kind=="idle" and not pending:
            require(page["path"]=="/lobby" and page.get("readySeaSalt") is True,
                    "大厅玩法、模式或海盐折纸入口未就绪："+str(raw))
            out={"batch":args.batch,"pin":pin,"site":"BGA","hostId":"99769903","checkedAt":before[0]["at"],
                 "loggedIn":True,"noActiveTable":True,"noPendingQueue":True,"noPenalty":True,
                 "source":raw.name,"sourceSHA256":digest(raw)}
            if evidence:out["noticeEvidence"]=evidence
            with lock():
                available()
                require(not summary.exists(),"另一份本批结果已发布",6)
                temp=batch/("预检-"+uuid.uuid4().hex+".tmp")
                try:
                    with temp.open("xb") as stream:
                        stream.write(json.dumps(out,ensure_ascii=False,indent=2).encode("utf-8"))
                        stream.flush();os.fsync(stream.fileno())
                    os.rename(temp,summary)
                finally:
                    temp.unlink(missing_ok=True)
            print("只读预检通过 "+args.batch+" 原件 "+str(raw))
            return 0
        require(kind!="table-or-queue","当前牌桌、匹配或队列优先，不处置："+str(raw))
        require(not page.get("need"),"本人待办："+str(page.get("need"))+"；原件 "+str(raw),8)
        mode="cooldown-recheck" if kind=="idle" and pending else "auto"
        observed="-" if mode=="cooldown-recheck" else page.get("observationId")
        require(mode=="cooldown-recheck" or kind in ("ordinary","ordinary-dialog","cooldown","security") and observed and
                (page.get("eligible") is True or kind=="cooldown"),"提示用途、入口或官方期限未核实："+details(rows)+"；原件 "+str(raw))
        key=(mode,observed,pending[-1]["data"]["deadline"] if pending else None)
        require(key not in handled,"同一提示已核过，未再次点击；原件 "+str(raw),4)
        handled.add(key)
        action,events,code=child(mode,observed)
        evidence.extend([{"file":f.name,"sha256":digest(f)} for f in (raw,action)])
        require(events[0]["data"]["pid"] not in child_pids,"处置进程与发现进程重复",6)
        child_pids.add(events[0]["data"]["pid"])
        if code not in (0,7):
            if code==9:
                print("冷却仍有效，按新期限运行 cooldown-recheck 后再次预检。")
            raise Refusal(code or 6,"处置未通过："+details(events)+"；原件 "+str(action))
        print("已核实本次处置，正在新进程复核。原件 "+str(action))
    raise Refusal(5,"超过本次顺序复核上限；保留原件，未发布本批预检")


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--batch",required=True)
    parser.add_argument("--exe",type=Path,required=True)
    parser.add_argument("--fixture",type=Path)
    try:
        args=parser.parse_args()
        require(re.fullmatch("[0-9a-f]{32}",args.batch),"批号无效")
        with inspecting(args.batch):
            sys.exit(main(args))
    except Refusal as error:
        print(str(error),file=sys.stderr); sys.exit(error.code)
    except (OSError,ValueError,KeyError) as error:
        print("预检拒绝："+str(error),file=sys.stderr);sys.exit(6)


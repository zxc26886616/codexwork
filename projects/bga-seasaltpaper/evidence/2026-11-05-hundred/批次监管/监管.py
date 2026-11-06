"""正式账号逐批监管。无定时开局、无自动补跑。Windows / Python 3.12。"""
import argparse
from contextlib import contextmanager
import ctypes
from ctypes import wintypes as w
from datetime import datetime, timezone
import hashlib
import json
import msvcrt
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid
import allocated_seat
import build_upgrade
import cancel_review
import start_refresh
import notice_recovery

HERE = Path(__file__).resolve().parent
CAMPAIGN = HERE.parent
ROOT = CAMPAIGN.parents[1]
STORE = HERE / "批次"
PIN = build_upgrade.PIN
CANDIDATE = ROOT / "dist/candidates" / build_upgrade.CANDIDATE
EXE = CANDIDATE / "bin/SeatControl.exe"
DATA = Path(os.environ["LOCALAPPDATA"]) / "SeaSaltDesk"
HOST = "99769903"
SUFFIXES = (".json", ".status.json", ".log", ".incidents.jsonl", ".ends.jsonl",
            ".fallback.jsonl", ".stop-queue", ".score-ack-hold")
K = ctypes.WinDLL("kernel32", use_last_error=True)
K.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, w.LPVOID, w.DWORD, w.DWORD, w.HANDLE]
K.CreateFileW.restype = w.HANDLE
K.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
K.OpenProcess.restype = w.HANDLE
K.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
K.CloseHandle.argtypes = [w.HANDLE]
K.CreateMutexW.argtypes = [w.LPVOID,w.BOOL,w.LPCWSTR]
K.CreateMutexW.restype = w.HANDLE
K.ReleaseMutex.argtypes = [w.HANDLE]

def require(condition, message):
    if not condition:
        raise ValueError(message)

def now():
    return datetime.now(timezone.utc).isoformat()

def batch_dir(batch):
    require(re.fullmatch(r"[0-9a-f]{32}", batch), "批次编号无效")
    return STORE / batch

def table_id(value):
    return isinstance(value, str) and re.fullmatch(r"[1-9][0-9]{0,17}", value)

def shared_read(path, offset=0):
    # FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE. 产品可随时 Move/Replace。
    handle = K.CreateFileW(str(path), 0x80000000, 7, None, 3, 0x80, None)
    if handle == w.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
    except Exception:
        K.CloseHandle(handle)
        raise
    with os.fdopen(fd, "rb") as stream:
        if offset < 0:
            stream.seek(max(0,stream.seek(0,os.SEEK_END)+offset))
        else:
            stream.seek(offset)
        return stream.read()

def get(path):
    return json.loads(shared_read(path).decode("utf-8-sig"))

def sha(path):
    return hashlib.sha256(shared_read(path)).hexdigest().upper()

def publish(path, payload, replace=False):
    raw = payload if isinstance(payload, bytes) else json.dumps(
        payload, ensure_ascii=False, indent=2).encode("utf-8")
    temp = path.with_name("." + uuid.uuid4().hex + ".tmp")
    try:
        with temp.open("xb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        if replace:
            os.replace(temp, path)
        else:
            os.rename(temp, path)  # Windows: exclusive publish
    finally:
        temp.unlink(missing_ok=True)

def once(path, payload):
    try:
        publish(path, payload)
        return True
    except FileExistsError:
        return False

def event(d, kind, **details):
    # 单次追加一条；worker 事件来自真实的 Popen/poll，原样交给独立核账。
    raw = json.dumps(dict(at=time.time(), kind=kind, **details), ensure_ascii=False)
    with (d / "监督.jsonl").open("ab", buffering=0) as stream:
        stream.write((raw + "\n").encode("utf-8"))

@contextmanager
def lock(path):
    with path.open("a+b") as stream:
        stream.seek(0)
        msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)

def held(path):
    try:
        shared_read(path)
    except FileNotFoundError:
        return False
    except OSError as error:
        if error.winerror in (32, 33):
            return True
        raise
    return False

def verify_files():
    manifest = CANDIDATE / "candidate.json"
    require(sha(manifest) == PIN, "固定清单 pin 不符")
    files = get(manifest)
    expected = {"candidate.json": PIN}
    for group in ("source", "bin", "integration"):
        expected.update({group + "/" + name: value for name, value in files[group].items()})
    actual = {p.relative_to(CANDIDATE).as_posix(): sha(p)
              for p in CANDIDATE.rglob("*") if p.is_file()}
    require(expected == actual, "固定包文件清单或 SHA256 不符")

def verify_build():
    verify_files()
    if PIN != build_upgrade.OLD_PIN:
        # 新品的报告以唯一换版票为索引，tickets/prepare 必须核验 E 原件；保留旧验收文件。
        return
    gate = get(CAMPAIGN / "固定包验收.json")
    require(gate["pin"] == PIN and gate["fullP0Passed"] is True, "固定包尚未通过完整 P0 验收")
    report = (ROOT / gate["offlineReport"]).resolve()
    require(report.is_relative_to(ROOT / "evidence") and sha(report) == gate["offlineReportSha256"].upper(),
            "固定包验收报告指纹不符")

def ledger():
    book = get(CAMPAIGN / "百局台账.json")
    total = book["total"]
    tables = book["normalTables"]
    require(type(total) is int and book["target"] == 100 and 0 <= total <= 100
            and len(tables) == total and len({r["table"] for r in tables}) == total
            and all(table_id(r["table"]) for r in tables), "累计台账数量或去重错误")
    return book

def quota(baseline):
    require(type(baseline) is int and 0 <= baseline < 100, "百局已满或累计无效")
    return min(3, 100 - baseline)

def notice_proof(batch, fresh=False):
    d = batch_dir(batch)
    return notice_recovery.proof(batch_dir(notice_recovery.PREVIOUS), STORE.parent/"处置复核"/d.name,
                                 build_upgrade.OLD_PIN, shared_read, fresh)

def upgrade_proof(batch):
    return build_upgrade.proof(ROOT, CAMPAIGN, STORE, batch_dir(batch).name, shared_read)

def select_product(first, expected_pin, expected_sha):
    # 一个 CLI 进程只控制当前命令所绑定的产品；既有旧票不会因后来换版而指向新 EXE。
    global PIN,CANDIDATE,EXE
    row,mark=build_upgrade.record(STORE,first,shared_read)
    require(row["pin"]==expected_pin and mark==expected_sha,"票据所属换版记录或产品指纹不符")
    CANDIDATE=build_upgrade.product(ROOT,row,shared_read)
    PIN,EXE=row["pin"],CANDIDATE/"bin/SeatControl.exe"

def cancel_proof(previous, successor, fresh=False):
    return cancel_review.proof(batch_dir(previous), STORE.parent/"撤桌复核"/batch_dir(successor).name,
                               shared_read, fresh)

def tickets(pending_notice=None, pending_upgrade=None, pending_cancel=None):
    # 不用时间戳排序。每次只能有一个直接后继，损坏或不完整目录一律拒绝。
    rows = []
    for d in STORE.iterdir():
        if not d.is_dir():
            continue
        require(d == batch_dir(d.name), "批次目录异常")
        t = get(d / "票据.json")
        require(t["batch"] == d.name,"票据身份不符")
        rows.append(t)
    chain = []
    prev = None
    baseline = 0
    upgrade = None
    while len(chain) < len(rows):
        next_rows = [t for t in rows if t["previous"] == prev]
        require(len(next_rows) == 1, "票据序列不连续")
        t = next_rows[0]
        require(t["baseline"] == baseline and t["count"] == quota(baseline)
                and 1 <= t["count"] <= 3, "票据额度不符")
        if upgrade is None:
            require(t["pin"]==build_upgrade.OLD_PIN and (PIN==build_upgrade.OLD_PIN or
                    t["batch"] in (notice_recovery.PREVIOUS,build_upgrade.PREVIOUS)),"历史首段票据不符")
        else:
            require((t["pin"],t.get("upgradeBatch"),t.get("upgradeReviewSha256"))==upgrade,
                    "新包票据未承接所在代的换版记录")
        chain.append(t)
        prev = t["batch"]
        closed = batch_dir(prev) / "结批.json"
        successors=[r for r in rows if r["previous"]==prev]
        need_switch=(successors and successors[0]["pin"]!=t["pin"]) or (not successors and pending_upgrade)
        if (batch_dir(prev) / "熔断.json").exists() and need_switch:
            require(closed.is_file() and get(closed)["fused"] is True and len(successors)<=1,"旧事故未结清或换版出现分叉")
            new_batch = successors[0]["batch"] if successors else pending_upgrade
            mark=upgrade_proof(new_batch)
            ruling,observed=build_upgrade.record(STORE,new_batch,shared_read)
            require(ruling["previous"]==prev and ruling["fromPin"]==t["pin"] and observed==mark,
                    "E 换版与此旧批及其固定包不符")
            upgrade=(ruling["pin"],new_batch,mark)
            baseline=get(closed)["auditedTotal"]
        elif (batch_dir(prev) / "熔断.json").exists() and t["pin"] != build_upgrade.OLD_PIN:
            require(closed.is_file() and len(successors)<=1,"受阻批次已有分叉或尚未结批")
            next_batch=successors[0]["batch"] if successors else pending_cancel
            require(next_batch is not None,"固定包已熔断；仅经独立核实的开局前撤桌可申请新批")
            receipt,baseline=cancel_proof(prev,next_batch,fresh=not successors)
            if successors:
                require(successors[0].get("cancelReviewSha256")==receipt,"续接票未绑定撤桌实证")
        elif (batch_dir(prev) / "熔断.json").exists():
            require(prev == notice_recovery.PREVIOUS and closed.exists(), "固定构建已熔断，须调查并重新验收")
            successors = [r for r in rows if r["previous"] == prev]
            if successors:
                require(len(successors) == 1 and successors[0].get("noticeReviewSha256") ==
                        notice_proof(successors[0]["batch"]), "旧提醒处置证据与唯一后继不符")
            else:
                require(pending_notice is not None and notice_proof(pending_notice, fresh=True),
                        "旧提醒须另有已核验处置证据和新批次号")
            require(get(closed)["auditedTotal"] == baseline, "旧提醒批次累计发生变化")
        elif closed.exists():
            baseline = get(closed)["auditedTotal"]
            require(baseline == t["baseline"] + t["count"], "前批未达到正常验收局数")
        else:
            require(len(chain) == len(rows), "未结批次已有后继")
    return chain, baseline

def prepare(notice_batch=None, upgrade_batch=None, cancel_batch=None):
    require(not notice_batch or PIN == build_upgrade.OLD_PIN, "旧固定包已停止新排")
    STORE.mkdir(exist_ok=True)
    with lock(STORE / "监管.lock"):
        chain, baseline = tickets(notice_batch,upgrade_batch,cancel_batch)
        prev = chain[-1]["batch"] if chain else None
        require(not prev or (batch_dir(prev) / "结批.json").exists(), "上一批仍未关闭")
        book = ledger()
        require(book["assessedBatch"] == prev and book["total"] == baseline < 100, "台账基线不符或百局已满")
        batch = notice_batch or upgrade_batch or cancel_batch or uuid.uuid4().hex
        review = None
        upgrade = None
        if upgrade_batch is not None:
            require(prev is not None and sha(CAMPAIGN/"百局台账.json")==get(batch_dir(prev)/"结批.json")["ledgerSha256"],
                    "须从已独立核账的当前旧事故首次换版")
            upgrade = (batch,upgrade_proof(batch))
            row,_=build_upgrade.record(STORE,batch,shared_read)
            require(row["previous"]==prev and row["fromPin"]==chain[-1]["pin"],"试图从其他事故签发")
            select_product(batch,row["pin"],upgrade[1])
        elif PIN != build_upgrade.OLD_PIN:
            require(chain and chain[-1]["pin"]!=build_upgrade.OLD_PIN, "需要已签发的换版票据")
            upgrade = (chain[-1]["upgradeBatch"],chain[-1]["upgradeReviewSha256"])
            select_product(upgrade[0],chain[-1]["pin"],upgrade[1])
        verify_build()
        if notice_batch is not None:
            require(prev == notice_recovery.PREVIOUS and sha(CAMPAIGN / "百局台账.json") == notice_recovery.LEDGER,
                    "原批次已被续用或其零局审核台账已变更")
            review = notice_proof(batch, fresh=True)
        cancel_receipt=None
        if cancel_batch is not None:
            require(prev is not None and chain[-1]["pin"]==PIN and
                    sha(CAMPAIGN/"百局台账.json")==get(batch_dir(prev)/"结批.json")["ledgerSha256"],
                    "取消续接须基于最新原批结账")
            cancel_receipt,accepted_total=cancel_proof(prev,batch,fresh=True)
            require(accepted_total==baseline,"撤桌续接累计不符")
        d = batch_dir(batch)
        d.mkdir()
        t = dict(batch=batch, pin=PIN, count=quota(baseline), baseline=baseline,
                 previous=prev, at=now())
        if review:
            t["noticeReviewSha256"] = review
        if upgrade:
            t["upgradeBatch"],t["upgradeReviewSha256"] = upgrade
        if cancel_receipt:
            t["cancelReviewSha256"]=cancel_receipt
        publish(d / "票据.json", t)
        return t

def preflight(d, pre):
    age = (datetime.now(timezone.utc) - datetime.fromisoformat(pre["checkedAt"])).total_seconds()
    require(pre["batch"] == d.name and pre["pin"] == PIN and pre["site"] == "BGA"
            and pre["hostId"] == HOST and 0 <= age <= 90
            and all(pre[key] is True for key in ("loggedIn", "noActiveTable", "noPendingQueue", "noPenalty")),
            "需要本批 90 秒内的正式账号空桌只读预检")
    raw = (d / pre["source"]).resolve()
    require(raw.parent == d and sha(raw) == pre["sourceSHA256"].upper(), "大厅原始预检指纹不符")

def validate(d, launch=False):
    chain, baseline = tickets()
    t = chain[-1]
    if t["pin"]!=build_upgrade.OLD_PIN:
        select_product(t["upgradeBatch"],t["pin"],t["upgradeReviewSha256"])
    verify_build()
    book = ledger()
    require(t["batch"] == d.name and t["pin"] == PIN and not (d / "结批.json").exists()
            and t["baseline"] == baseline == book["total"] and book["assessedBatch"] == t["previous"],
            "已有其他批次或审核基线变化")
    require(DATA.is_dir() and not any(k.startswith("WEBVIEW2_") for k in os.environ), "正式资料或 WebView 环境异常")
    require(not held(DATA / "series-run.lock"), "即时制账号正在使用")
    require(not any((DATA / "series").glob(d.name + ".*")), "该批次已有正式资料")
    pre,binding=start_refresh.select(d,PIN,shared_read) if launch else (get(d/"大厅预检.json"),None)
    preflight(d,pre)
    if "upgradeBatch" in t:
        ruling = get(STORE.parent/"换版复核"/t["upgradeBatch"]/"E换版.json")
        require(timestamp(pre["checkedAt"]) >= timestamp(ruling["reviewedAt"]),
                "本批预检必须晚于新包 E 换版验收")
    if "cancelReviewSha256" in t:
        ruling=get(STORE.parent/"撤桌复核"/d.name/"E撤桌.json")
        require(timestamp(pre["checkedAt"])>=timestamp(ruling["reviewedAt"]),
                "本批预检须晚于独立撤桌核验")
    if "noticeReviewSha256" in t:
        # 原提醒经处置后的新批另做正式预检，不能复制动作前的假阴性大厅记录。
        approval = get(STORE.parent/"处置复核"/d.name/"E复核.json")
        require(timestamp(pre["checkedAt"]) >= timestamp(approval["reviewedAt"]),
                "本批大厅预检必须晚于独立处置复核")
    return t,binding

def release(d, pin):
    with lock(STORE / "监管.lock"):
        t,_ = validate(d)
        require(pin == PIN, "本次放行 pin 不符")
        publish(d / "E放行.json", dict(batch=d.name, pin=PIN, count=t["count"], at=now(),
                ticketSha256=sha(d / "票据.json"), preflightSha256=sha(d / "大厅预检.json")))

def authorization(d, t):
    row = get(d / "E放行.json")
    require(row["batch"] == d.name and row["pin"] == PIN and row["count"] == t["count"]
            and row["ticketSha256"] == sha(d / "票据.json"), "放行票据/批次不符")

def detach(*args):
    return subprocess.Popen([sys.executable, "-B", str(Path(__file__).resolve()), *args],
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            creationflags=subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP)

def start(d):
    with lock(STORE / "监管.lock"):
        require(not (d/"消费.json").exists(),"本次授权已经消费")
        t,binding = validate(d,launch=True)
        authorization(d, t)
        publish(d / "消费.json", dict(at=now(), batch=d.name, pin=PIN, count=t["count"],launchBinding=binding))
        return dict(batch=d.name, supervisor=detach("_serve", d.name).pid, count=t["count"])

def data_file(d, suffix):
    return DATA / "series" / (d.name + suffix)

def read_if(path, default):
    try:
        return get(path)
    except FileNotFoundError:
        return default

def official_ended(d, table, state):
    if not table_id(table):
        return False
    if state.get("id") == d.name and state.get("site") == "BGA" and state.get("hostId") == HOST:
        if any(row["table"] == table for row in state.get("rounds", [])):
            return True
    for suffix in (".ends.jsonl", ".fallback.jsonl"):
        path = data_file(d, suffix)
        if not path.exists():
            continue
        for raw in shared_read(path).split(b"\n")[:-1]:
            row = json.loads(raw)
            if row.get("batch") != d.name or row.get("site") != "BGA" or row.get("table") != table:
                continue
            if row.get("status") in ("finished", "archive") or (row.get("code") == "supervisor-fallback-ended"
                    and row.get("self") == HOST and row["details"].get("OfficialStatus") in ("finished", "archive")):
                return True
    return False

def last_sample(path):
    try:
        raw = shared_read(path, -32768)
    except FileNotFoundError:
        return {}
    lines = raw.split(b"\n")[:-1]
    return json.loads(lines[-1]) if lines else {}

def timestamp(iso):
    return datetime.fromisoformat(iso).timestamp()

def snapshot(d, table=None):
    state = read_if(data_file(d, ".json"), {})
    status = read_if(data_file(d, ".status.json"), {})
    table = table or state.get("currentTable") or state.get("offerTable") or status.get("table")
    clock = {}
    if table_id(table):
        try:
            raw = shared_read(DATA / "seat-actions" / f"BGA-{table}-{HOST}.clock.jsonl")
        except FileNotFoundError:
            raw = b""
        lines = raw.split(b"\n")[:-1]
        for line in reversed(lines):
            row = json.loads(line)
            require(row["table"] == f"BGA/{table}/座位/{HOST}", "棋钟归属不符")
            if not clock:
                fresh = (row["ClockStatus"] == "known" and row.get("ClockSource") == "timeToThink_"+HOST
                         and row.get("opponent") == state.get("opponentId")
                         and 0 <= time.time()-timestamp(row["ClockReadAt"]) <= 5)
                clock.update(status=row["ClockStatus"], seconds=row["ClockSeconds"] if fresh else None,
                             readAt=row["ClockReadAt"])
            if (row["ClockStatus"] == "known" and row["ClockSeconds"] is not None
                and row.get("ClockSource") == "timeToThink_"+HOST and row.get("opponent") == state.get("opponentId")):
                clock.update(lastKnownSeconds=row["ClockSeconds"], lastKnownAt=row["ClockReadAt"])
                break
    return dict(batch=d.name, table=table, ownClock=clock or None,
                workerStatus=status.get("status"), completed=state.get("completed"),
                queuePending=state.get("queuePending"), officialEnded=official_ended(d, table, state))

def fuse(d, reason, table=None, takeover=False, evidence=None, official=None):
    initial=dict(batch=d.name,pin=PIN,at=now(),reason=reason)
    if official is not None:
        initial.update(table=table,officialEvidence=official)
    once(d / "熔断.json", initial)
    try:
        view = snapshot(d, table)
    except (OSError, ValueError, KeyError) as ex:
        # 损坏的观测文件不能挡住停排；棋钟未知时不给出估算秒数。
        view = dict(batch=d.name, table=table, ownClock=None, officialEnded=False, readError=str(ex))
    takeover = takeover and not view["officialEnded"]
    alert = dict(at=now(), reason=reason, takeover=takeover, clockEvidence=evidence, **view)
    if official is not None:
        alert["officialEvidence"]=official
    if takeover and once(d / "需接管.json", alert):
        event(d, "handoff", table=view["table"], reason=reason, ownClock=view["ownClock"])
    if once(d / "首次警报.json", alert):
        event(d, "stop-request", table=view["table"], reason=reason, ownClock=view["ownClock"])
    stop_worker(d)
    return alert

def stop_worker(d):
    # 通过同一原包的入队互斥锁设闩。绝不关闭桌面、挂起出牌或重启对局。
    if data_file(d, ".stop-queue").exists():
        return True
    if not data_file(d, ".json").exists():
        return False
    verify_files()
    result = subprocess.run([EXE, "--stop-queue", d.name, "--root", DATA], timeout=8,
                            capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    return result.returncode == 0 and data_file(d, ".stop-queue").exists()

class Watch:
    def __init__(self, d, recovering=False):
        self.d = d
        self.recovering = recovering
        self.offsets = {}
        self.tables = set()
        self.low, self.stalled = {}, {}
        self.state_stamp, self.state = None, {}
        self.intent_key, self.first_sample = None, None
        self.admissions=0
        self.unclassified_stop_at=None

    def admission(self, progress):
        # 与原包 WithQueueGate 使用同一 Windows 命名互斥体。只新增明确归属于本批的预算闩。
        d=self.d
        self.admissions+=1
        count=get(d/"票据.json")["count"]
        latest=re.fullmatch(r"已完成 (\d+)/(\d+)",progress)
        if not latest or int(latest[2])!=count or self.admissions!=int(latest[1])+1 or self.admissions>count:
            fuse(d,"admission-count-conflict")
            return
        if self.admissions!=count:
            return
        handle=K.CreateMutexW(None,False,"Local\\SeaSaltDesk.Queue."+d.name)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            require(K.WaitForSingleObject(handle,5000) in (0,0x80),"额度门无法取得原包入队锁")
            try:
                if once(data_file(d,".stop-queue"),cancel_review.quota_signal(d.name,count)):
                    event(d,"admission-cap",count=count)
            finally:
                K.ReleaseMutex(handle)
        finally:
            K.CloseHandle(handle)

    def clock_risk(self):
        d = self.d
        statefile = data_file(d, ".json")
        if not statefile.exists():
            return
        stamp = statefile.stat().st_mtime_ns
        if self.state_stamp != stamp:
            self.state = get(statefile)
            self.state_stamp = stamp
        s = self.state
        table, opponent = s.get("currentTable"), s.get("opponentId")
        if not (table_id(table) and table_id(opponent) and s.get("id") == d.name and
                s.get("site") == "BGA" and s.get("hostId") == HOST and s.get("realtime") is True):
            self.intent_key = None
            return
        prefix = DATA / "seat-actions" / f"BGA-{table}-{HOST}"
        row = last_sample(prefix.with_suffix(".clock.jsonl"))
        seat = f"BGA/{table}/座位/{HOST}"
        # 必须已有同桌已发意图。只思考、轮次变化与被动等待不会触发本门槛。
        secs = row.get("ClockSeconds")
        if not (row.get("table") == seat and row.get("opponent") == opponent and row.get("generation")
                and row.get("ClockStatus") == "known" and row.get("ClockSource") == "timeToThink_"+HOST
                and row.get("PhaseSource") == "last-server-state" and row.get("ClockPhase")
                and type(secs) is int and row.get("pendingIntent")):
            self.intent_key = None
            return
        progress = last_sample(prefix.with_suffix(".progress.jsonl"))
        read_at = timestamp(row["ClockReadAt"])
        if (progress.get("Table") == seat and progress.get("Opponent") == opponent and
            (progress.get("Id") == row["pendingIntent"] or timestamp(progress["at"]) >= read_at
             or progress.get("Generation",0) > row["generation"])):
            self.intent_key = None
            return
        intent = (seat,opponent,row["generation"],row["ClockPhase"],row["pendingIntent"])
        if self.intent_key != intent:
            self.intent_key, self.first_sample = intent, read_at
        locked = row.get("WaitReason") == "interface-lock" and row.get("waitMilliseconds",0) >= 5000
        if not locked and read_at - self.first_sample < 5:
            return
        age = time.time() - read_at
        if age < 0 or official_ended(d, table, s):
            return
        reported = row.get("verifiedDeadlineRemainingMs")
        # 冻结后不能把历史秒数当作实读；只以原程序已验证截止时间作待核预警。
        projected = secs-age
        fresh_low = secs <= 30 and age <= 5
        verified = (type(reported) is int and abs(reported/1000-secs) <= 2 and projected <= 30
                    and age <= max(5,secs+5))
        if not fresh_low and not verified:
            return
        evidence = dict(sampleSeconds=secs, sampleAt=row["ClockReadAt"], phase=row["ClockPhase"],
                        generation=row["generation"], pendingIntent=row["pendingIntent"],
                        estimatedSeconds=max(0,int(projected)) if verified else None)
        alert = fuse(d, "stalled-own-clock-at-risk", table, fresh_low, evidence)
        if not fresh_low:
            once(d / "需核实原桌.json", alert)

    def lines(self, suffix):
        offset = self.offsets.get(suffix, 0)
        try:
            raw = shared_read(data_file(self.d, suffix), offset)
        except FileNotFoundError:
            return []
        size = raw.rfind(b"\n") + 1
        self.offsets[suffix] = offset + size
        return raw[:size].decode("utf-8-sig").splitlines()

    def check(self, live=False):
        d = self.d
        if not self.recovering and not (d / "需接管.json").exists():
            self.clock_risk()
        for suffix in (".incidents.jsonl", ".fallback.jsonl"):
            for line in self.lines(suffix):
                incident = json.loads(line)
                require(incident["batch"] == d.name and incident["site"] == "BGA", "事故归属不符")
                code, table = incident["code"], incident.get("table")
                if table_id(table):
                    self.tables.add(table)
                if code==allocated_seat.CODE:
                    state=read_if(data_file(d,".json"),{})
                    bound=state.get("currentTable") or state.get("offerTable")
                    proof=allocated_seat.official_play(incident,d.name,bound,HOST,state.get("opponentId"))
                    if (proof and state.get("id")==d.name and state.get("site")=="BGA" and
                        state.get("hostId")==HOST and state.get("opponentId")==proof["opponent"]):
                        fuse(d,code,bound,True,official=proof)
                    else:
                        alert=fuse(d,code,bound)
                        if not alert.get("officialEnded"):
                            once(d/"需核实原桌.json",alert)
                elif code not in ("page-read-slow", "page-read-returned"):
                    fuse(d, code, table, code in ("draw-receipt-handoff-required", "platform-penalty-seat-revoked"))
        for line in self.lines(".log"):
            parts = line.split("　", 3)
            if len(parts) < 4:
                continue
            at, progress, table, note = parts
            if not self.recovering and any(s in note for s in ("即时制已入队", "已绑定匹配桌")):
                event(d, "observation", table=table, message=note, logAt=at)
            if live and "即时制已入队" in note:
                self.admission(progress)
            if not table_id(table):
                continue
            self.tables.add(table)
            when = datetime.strptime(at, "%Y-%m-%d %H:%M:%S").timestamp()
            if any(s in note for s in ("等待对手或服务器", "等待其他座位行动", "等待服务器切换回合或结算",
                                      "本局已结束", "gameEnd", "已读取官方结算")):
                self.low.pop(table, None)
                self.stalled.pop(table, None)
            match = re.search(r"己方棋钟 (-?\d+) 秒", note)
            if match and int(match[1]) <= 2:
                self.low[table] = when
            if any(s in note for s in ("己方回合尚未进展", "撤销旧文档", "关闭旧出牌文档", "上一步回执待核对")):
                self.stalled[table] = when
            danger = (table in self.low and table in self.stalled
                      and when - min(self.low[table], self.stalled[table]) <= 3 and 0 <= time.time()-when <= 5)
            handoff = any(s in note for s in ("需要接管原桌", "需要立即接管", "需要人工接管", "请接管", "请立即接管"))
            failed = any(s in note for s in ("已停止操作", "自动出牌已暂停", "无法核实原桌", "无法核实上一次",
                                              "官方终局属于", "未知终局", "已失权，已停止新匹配"))
            if danger or failed or handoff:
                fuse(d, "own-clock-stalled" if danger else "table-handoff-or-failure", table,
                     danger or failed or handoff)
        if (d / "熔断.json").exists():
            stop_worker(d)
        elif data_file(d, ".stop-queue").exists() and shared_read(data_file(d,".stop-queue")) != \
                cancel_review.quota_signal(d.name,get(d/"票据.json")["count"]):
            # 产品先落停排再写同一事故的 JSONL。闩已生效，给下一次轮询机会取得具体接管依据。
            if self.unclassified_stop_at is None:
                self.unclassified_stop_at=time.monotonic()
            if not live or time.monotonic()-self.unclassified_stop_at>=1:
                fuse(d, "product-stop-queue")

    def archive(self):
        d = self.d
        self.check()
        state = read_if(data_file(d, ".json"), {})
        self.tables.update(r["table"] for r in state.get("rounds", []))
        self.tables.update([state.get("currentTable"), state.get("offerTable")])
        sources = [data_file(d, suffix) for suffix in SUFFIXES]
        for table in filter(table_id, self.tables):
            sources.append(DATA / "matches" / f"BGA-{table}.json")
            for suffix in ("clock.jsonl", "progress.jsonl", "json"):
                sources.append(DATA / "seat-actions" / f"BGA-{table}-{HOST}.{suffix}")
        for path in sources:
            if path.exists():
                publish(d / path.name, shared_read(path), replace=True)

def consumed_start(d):
    t,binding=validate(d,launch=True)
    authorization(d,t)
    receipt=get(d/"消费.json")
    need_binding=receipt.get("launchBinding")
    require(receipt["batch"]==d.name and receipt["pin"]==PIN and receipt["count"]==t["count"]
            and (need_binding==binding if need_binding is not None else "refreshSha256" not in binding),
            "启动凭证与已消费授权不符")
    return t

def run_worker(d):
    with lock(d / "监视.lock"):
        with lock(STORE / "监管.lock"):
            t = consumed_start(d)
            publish(d / "发车意图.json", dict(at=now(), supervisor=os.getpid()))
        # 独立看门进程先打开本进程句柄，掉线也能保留原桌、停止新匹配。
        sentinel = detach("_sentinel", d.name, "--parent", str(os.getpid()))
        deadline = time.monotonic() + 10
        while not (d / "守护就绪.json").exists():
            require(sentinel.poll() is None and time.monotonic() < deadline, "独立看门进程未就绪")
            time.sleep(.05)
        with lock(STORE / "监管.lock"):
            consumed_start(d)
            require(sentinel.poll() is None, "守护进程已退出")
            proc = subprocess.Popen([EXE, "--realtime", str(t["count"]), "--site", "BGA",
                                     "--batch-id", d.name, "--root", DATA],
                                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
            publish(d / "worker.json", dict(batch=d.name, pin=PIN, pid=proc.pid, at=now(), exe=str(EXE)))
            event(d, "worker", batch=d.name, pin=PIN, pid=proc.pid, exe=str(EXE), exeSHA256=sha(EXE))
        watch = Watch(d)
        while proc.poll() is None:
            if sentinel.poll() is not None:
                fuse(d, "sentinel-exited")
            watch.check(live=True)
            time.sleep(.25)
        watch.archive()
        final = read_if(data_file(d, ".status.json"), {})
        if proc.returncode != 0 or final.get("status") != "completed":
            alert = fuse(d, "worker-exited-without-normal-completion", final.get("table"))
            if table_id(alert["table"]) and not alert["officialEnded"]:
                once(d / "需核实原桌.json", alert)
        event(d, "worker-exit", pid=proc.pid, exitCode=proc.returncode)
        publish(d / "退出.json", dict(at=now(), exitCode=proc.returncode))

def recover(d):
    require((d / "消费.json").exists(), "没有待恢复的启动意图")
    if (d / "退出.json").exists():
        return
    with lock(d / "监视.lock"):
        # 即使启动进程死在 Popen 与 PID 落盘之间，也永远只重接观察，不再发车。
        fuse(d, "supervision-interrupted")
        watch = Watch(d, recovering=True)
        quiet_since = time.monotonic()
        while True:
            watch.check()
            if held(data_file(d, ".run.lock")):
                quiet_since = time.monotonic()
            elif time.monotonic() - quiet_since >= 12:
                break
            time.sleep(.25)
        watch.archive()
        worker = read_if(d / "worker.json", {})
        if worker:
            event(d, "worker-exit", pid=worker["pid"], exitCode=None, recovered=True)
        publish(d / "退出.json", dict(at=now(), exitCode=None, recovered=True, requiresReview=True))

def sentinel(d, parent):
    require(get(d / "发车意图.json")["supervisor"] == parent == os.getppid(), "守护父进程身份不符")
    handle = K.OpenProcess(0x100000, False, parent)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        publish(d / "守护就绪.json", dict(at=now(), pid=os.getpid(), parent=parent))
        require(K.WaitForSingleObject(handle, 0xffffffff) == 0, "父进程监督句柄异常")
    finally:
        K.CloseHandle(handle)
    if not (d / "退出.json").exists():
        recover(d)

def close(d, audited):
    with lock(STORE / "监管.lock"):
        book, t = ledger(), get(d / "票据.json")
        require((d / "退出.json").exists() and not held(data_file(d, ".run.lock")), "原进程仍未结清")
        rows = [r for r in book["normalTables"] if r["batch"] == d.name]
        audits = [r for r in book["batches"] if r["batch"] == d.name]
        require(book["assessedBatch"] == d.name and audited == book["total"] == t["baseline"] + len(rows)
                and len(rows) <= t["count"] and len(audits) == 1 and audits[0]["pin"] == PIN
                and set(audits[0]["normal"]) == {r["table"] for r in rows},
                "本批独立核对结果与累计不一致")
        state = read_if(d / (d.name + ".json"), {})
        normal = (audits[0]["clean"] is True and read_if(d / "退出.json", {}).get("exitCode") == 0 and len(rows) == t["count"]
                  and state.get("site") == "BGA" and state.get("id") == d.name and state.get("hostId") == HOST
                  and state.get("planned") == t["count"] and state.get("finished") is True
                  and not any(state.get(k) for k in ("queuePending", "currentTable", "offerTable"))
                  and {r["table"] for r in rows} == {r["table"] for r in state.get("rounds", [])})
        if not normal:
            fuse(d, "batch-did-not-pass-normal-review")
        publish(d / "结批.json", dict(at=now(), auditedTotal=audited, accepted=len(rows),
                ledgerSha256=sha(CAMPAIGN / "百局台账.json"), fused=(d / "熔断.json").exists()))

def status(d):
    return {**snapshot(d), **{key: read_if(d / path, None) for key, path in
            (("ticket","票据.json"), ("worker","worker.json"), ("alert","首次警报.json"),
             ("handoff","需接管.json"), ("verifyTable","需核实原桌.json"),
             ("exit","退出.json"), ("closed","结批.json"))}}

def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("prepare")
    for name in ("prepare-after-cancel", "prepare-after-upgrade", "prepare-after-notice", "allow", "start", "status", "stop", "recover", "close", "_serve", "_sentinel", "wait"):
        item = sub.add_parser(name)
        item.add_argument("batch")
        if name == "allow":
            item.add_argument("--pin", required=True)
        elif name == "close":
            item.add_argument("--audited-total", required=True, type=int)
        elif name == "_sentinel":
            item.add_argument("--parent", required=True, type=int)
        elif name == "wait":
            item.add_argument("--seconds", type=int, default=45)
    a = p.parse_args()
    if a.cmd == "prepare":
        return prepare()
    if a.cmd == "prepare-after-notice":
        return prepare(a.batch)
    if a.cmd == "prepare-after-upgrade":
        return prepare(upgrade_batch=a.batch)
    if a.cmd == "prepare-after-cancel":
        return prepare(cancel_batch=a.batch)
    d = batch_dir(a.batch)
    if a.cmd not in ("status","wait"):
        receipt=get(d/"票据.json")
        require(not (d/"结批.json").exists() and receipt["pin"]!=build_upgrade.OLD_PIN,
                "已结批及历史旧包只可读取")
        select_product(receipt["upgradeBatch"],receipt["pin"],receipt["upgradeReviewSha256"])
    if a.cmd == "allow":
        release(d, a.pin)
    elif a.cmd == "start":
        return start(d)
    elif a.cmd == "_serve":
        try:
            run_worker(d)
        except Exception as error:
            # 启动前准入失败亦封存消费，不复用。守护已就绪时由其接管。
            once(d / "监管异常.json", dict(at=now(), message=str(error)))
            if not (d / "退出.json").exists() and (d / "消费.json").exists():
                fuse(d, "supervisor-failed")
            raise
    elif a.cmd == "_sentinel":
        sentinel(d, a.parent)
    elif a.cmd == "stop":
        with lock(STORE / "监管.lock"):
            fuse(d, "operator-stop")
        return dict(stopConfirmed=stop_worker(d), **status(d))
    elif a.cmd == "recover":
        recover(d)
    elif a.cmd == "close":
        close(d, a.audited_total)
    elif a.cmd == "wait":
        until = time.monotonic() + max(0, a.seconds)
        while (time.monotonic() < until and not any((d / f).exists()
               for f in ("熔断.json", "需接管.json", "退出.json", "监管异常.json"))):
            time.sleep(.25)
    return status(d)

if __name__ == "__main__":
    try:
        print(json.dumps(main(), ensure_ascii=True), flush=True)
    except Exception as exc:
        sys.exit(str(exc))

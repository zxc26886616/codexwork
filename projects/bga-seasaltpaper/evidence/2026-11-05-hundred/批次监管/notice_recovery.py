"""只复核首批零入队处罚提醒；不能发起浏览器动作或更改旧闩锁。"""
from datetime import datetime, timezone
import hashlib
import json
import re

PREVIOUS = "03a0f0ae33594a0ebdec0b70acf72326"
ARCHIVE = "6B214F4588BFA3F7D8693959BF5E4C7926D9E7FC14461A92E2CED1349E819FDE"
LEDGER = "AFD826F7A01D1EFA2A84D9D53E01432686039166E4B2397E50B8F5A373E4349D"
NOTICE_TABLE = "942953060"
CANDIDATE_BATCH = "b86b83dbf7b64785a1e6e071a89df383"
FIRST_ACTION = "6C564DA68FAFA8D1FF246B3A593C6796FF496B49110BF42CAC186EDAA55CD335"
FIRST_LOBBY = "3697D1CA148358A7F985B443FBB21C1606BAC9D34EE081385C5D111977F6FBCC"
HOST = "99769903"
ORIGIN = "https://boardgamearena.com"
BLOCKED = re.compile(r"未能完成|未完成对局|惩罚|处罚|不能开始|无法开始|限制匹配|暂停匹配|返回游戏|继续游戏|轮到你|冷却|penalty|suspend|your turn|rejoin game", re.I)

def need(ok, reason):
    if not ok:
        raise ValueError("处置复核不通过：" + reason)

def digest(raw):
    return hashlib.sha256(raw).hexdigest().upper()

def when(iso):
    value = datetime.fromisoformat(iso)
    need(value.utcoffset() is not None, "时间缺少时区")
    return value

def archive_digest(old, read):
    rows = []
    for f in sorted(old.rglob("*"), key=lambda p: p.relative_to(old).as_posix()):
        if f.is_file():
            rows.append(digest(read(f)) + "  " + f.relative_to(old).as_posix() + "\n")
    return digest("".join(rows).encode("utf-8"))

def timeline(raw, sequence, navigation=False, exit_codes=(0,)):
    rows = [json.loads(line) for line in raw.decode("utf-8-sig").splitlines() if line.strip()]
    main = [r for r in rows if not navigation or r["stage"] not in ("navigation","navigation-complete")]
    need([r["stage"] for r in main] == sequence and rows[0] == main[0] and rows[-1] == main[-1],
         "缺少真实操作或顺序不符")
    dates = [when(r["at"]) for r in rows]
    need(dates == sorted(dates), "原始事件时间倒序")
    need(type(rows[0]["pid"]) is int and rows[0]["pid"] > 0 and rows[0]["pid"] == rows[-1]["pid"]
         and type(rows[-1]["exitCode"]) is int and rows[-1]["exitCode"] in exit_codes, "采集进程不完整")
    if navigation:
        before_at = when(main[1]["at"])
        nav = [r for r in rows if r["stage"].startswith("navigation") and when(r["at"]) > before_at]
        begin = next((i for i,r in enumerate(nav) if r["stage"] == "navigation" and r["path"] == "/welcome"), None)
        if nav:
            need(begin is not None and all(r["path"] in ("/welcome","/","/lobby") for r in nav) and
                 all(r["success"] is True and type(r["http"]) is int and 200 <= r["http"] < 300
                     for r in nav if r["stage"] == "navigation-complete") and
                 any(r["stage"] == "navigation-complete" for r in nav[begin+1:]), "顶层导航未成功完成")
    return main

def clean_page(page):
    need(page["origin"] == ORIGIN and page["user"]["id"] == HOST and page["user"]["name"] == "bke780",
         "正式站或登录身份不符")
    need(page["path"] != "/penalty" and not page["noticeTables"] and not page["queuedIds"] and not page["overlays"]
         and not any(r["text"] for r in page["progress"])
         and not BLOCKED.search(" ".join(page["noticeLines"] + page["headings"])), "仍有提醒或未清桌")

def clean_lobby(rows):
    p = rows[1]["page"]
    clean_page(p)
    need(type(rows[2]["blockedNavigation"]) is int and rows[2]["blockedNavigation"] == 0
         and p["path"] == "/lobby" and p["lobby"]["visible"] is True
         and {"rankingmode_simple","gamemode_realtime","opponentmode_automatic"} <= set(p["lobby"]["modes"])
         and any(c["id"] in ("Xannaplay_1631","wannaplay_1631") and c["disabled"] is False
                 and re.search(r"海盐折纸|Sea Salt",c["text"],re.I) and re.search(r"开始游戏|Play now",c["text"],re.I)
                 for c in p["controls"]), "最终大厅模式或海盐折纸入口不可用")

HEAD = "糟糕，看来你未能完成某一场游戏 :("
FAQ = ("如果我的名誉太低的话会怎样？", "如何恢复我的名誉？", "时间限制太短了", "我遇见了一些技术问题",
       "这是与朋友一起玩的游戏：为什么我会受到惩罚？", "我退出游戏是因为我受到辱骂／攻击了")

def notice(page):
    need(page["origin"] == ORIGIN and page["path"] == "/penalty" and page["user"] == {"id":HOST,"name":"bke780"}
         and page["headings"] == [HEAD] and not page["overlays"] and not page["queuedIds"]
         and not any(r["text"] for r in page["progress"]) and len(page["noticeTables"]) == 1,
         "须是同账号、无在室或排队、只有一项历史未完赛提醒")
    item = page["noticeTables"][0]
    need(item["text"] == "海盐折纸" and re.fullmatch(r"[1-9][0-9]{0,17}",item["table"])
         and re.fullmatch(r"海盐折纸\s*\(20\d\d年[A-Za-z]{3}\d{1,2}日 \d{1,2}:\d\d\)",item["context"]),
         "历史游戏桌号或日期不明")
    lines = list(page["noticeLines"])
    while lines and (lines[0].isdecimal() or lines[0] in ("Http Error: 403 Read-only","加载中... ...")):
        lines.pop(0)
    need(len(lines) >= 6 and lines[:2] == [HEAD,item["context"]]
         and re.fullmatch(r"名誉值惩罚\s*[:：]\s*[-−][1-9][0-9]{0,2}\s*☯(?:\s*（因为你近期又受到了另一项惩罚）)?",lines[2])
         and lines[3:6] == ["对每个人来说，未完成的对局视为败局。","协助我们让玩家们开心！","明白了！"]
         and all(line in FAQ for line in lines[6:]),
         "确认页出现其他限制、协议、承诺或未知提示")
    return item["table"], item["context"], lines[2]

def check_click(click, table):
    need(click["origin"] == ORIGIN and click["path"] == "/penalty" and click["hostId"] == HOST
         and click["noticeTable"] == table and click["text"] == "明白了！"
         and click["visible"] is True and click["enabled"] is True and click["dispatched"] is True
         and type(click["clickCount"]) is int and click["clickCount"] == 1, "缺少对本条提醒的一次真实确认")

def step_files(table):
    suffix = "" if table == NOTICE_TABLE else "-"+table
    return "处置"+suffix+".jsonl", "新会话大厅"+suffix+".jsonl"

def proof(old, folder, pin, read, fresh=False):
    # 这个摘要覆盖实际退出/停排/结批、原始批次/日志/监督、两个提醒来源等22件。
    # 该唯一旧批仅在大厅，零提交/在途/用户桌号，唯一事故为 platform-penalty。
    need(old.name == PREVIOUS and archive_digest(old, read) == ARCHIVE, "首批原始归档与已核对的零入队事故不同")
    approval_raw = read(folder/"E复核.json")
    approval = json.loads(approval_raw)
    need(approval["decision"] == "E_ACCEPT_NOTICE_CHAIN" and approval["previous"] == PREVIOUS
         and approval["batch"] == folder.name == CANDIDATE_BATCH and approval["pin"] == pin
         and approval["previousArchiveSha256"] == ARCHIVE
         and approval["lobbyRequestedUrl"] == ORIGIN+"/lobby", "未绑定本次精确提醒链、旧档与新批")
    steps = approval["steps"]
    need(isinstance(steps,list) and 2 <= len(steps) <= 32 and steps[0]["table"] == NOTICE_TABLE,
         "首条已确认，但已观察到的后继提醒尚未全部处置")
    old_probe = [json.loads(r) for r in read(old/"停排后账号只读-20261105-183622.jsonl").splitlines()]
    expected = notice(next(r["page"] for r in old_probe if r["stage"]=="before"))
    prior_read = when(old_probe[-1]["at"])
    seen, names = set(), set()
    for i, step in enumerate(steps):
        table = step["table"]
        need(table == expected[0] and table not in seen, "提醒承接错误、遗漏或重复确认")
        seen.add(table)
        act_file, lobby_file = step_files(table)
        names.update((act_file,lobby_file))
        action_raw, lobby_raw = read(folder/act_file), read(folder/lobby_file)
        need(digest(action_raw) == step["actionSha256"] and digest(lobby_raw) == step["lobbySha256"],
             "本条处置或独立读数与 E 审核指纹不同")
        if i == 0:
            need(step["actionSha256"] == FIRST_ACTION and step["lobbySha256"] == FIRST_LOBBY,
                 "必须沿用已单击的首条原件")
        final = i == len(steps)-1
        action = timeline(action_raw, ["background-start","before","action","after","closed"],
                          navigation=True, exit_codes=(0,) if final else (0,5))
        lobby = timeline(lobby_raw, ["background-start","before","read-only-complete","closed"])
        need(prior_read < when(action[0]["at"]) <= when(action[-1]["at"]) <= when(lobby[0]["at"])
             and when(lobby[-1]["at"]) <= when(step["reviewedAt"]) <= when(approval["reviewedAt"]),
             "原始处置与独立复核时序不符")
        need(lobby[0]["pid"] != action[0]["pid"] and type(lobby[2]["blockedNavigation"]) is int
             and lobby[2]["blockedNavigation"] == 0, "处置后缺少独立官方大厅入口实读")
        need(notice(action[1]["page"]) == expected, "操作前的提醒与上一独立会话不同")
        check_click(action[2],table)
        after, p = action[3]["page"], lobby[1]["page"]
        if not final:
            expected = notice(after)
            need(expected[0] not in seen and expected == notice(p),
                 "单击后未切换到同一项全新历史提醒，或新会话结果不一致")
        else:
            need(after["path"] in ("/","/welcome","/lobby"), "最后一条尚未处置或出现新提醒")
            clean_page(after)
            clean_lobby(lobby)
        prior_read = when(lobby[-1]["at"])
    need(names == {p.name for pattern in ("处置*.jsonl","新会话大厅*.jsonl") for p in folder.glob(pattern)},
         "处置目录存在未被 E 纳入的采集记录")
    extra = folder/"最新大厅复读.jsonl"
    if "refreshedLobbySha256" in approval or extra.exists():
        raw = read(extra)
        need(approval.get("refreshedLobbySha256") == digest(raw), "最新大厅复读未经 E 审核")
        newer = timeline(raw, ["background-start","before","read-only-complete","closed"])
        need(newer[0]["pid"] not in (lobby[0]["pid"],action[0]["pid"]) and prior_read < when(newer[0]["at"])
             and when(newer[-1]["at"]) <= when(approval["reviewedAt"]), "最新大厅复读的身份或时序不符")
        clean_lobby(newer)
        lobby = newer
    if fresh:
        now = datetime.now(timezone.utc)
        need(when(approval["reviewedAt"]) <= now and 0 <= (now-when(lobby[1]["at"])).total_seconds() <= 90,
             "最终大厅实证超过90秒或时间超前")
    return digest(approval_raw)

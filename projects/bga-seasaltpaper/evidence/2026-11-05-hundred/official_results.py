"""Read and sanitize one public BGA result. No profile, game-view, or write requests."""
from datetime import datetime, timezone
from http.cookiejar import CookieJar
import json
import re
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit, urlunsplit
from urllib.request import build_opener, HTTPCookieProcessor, HTTPRedirectHandler, ProxyHandler, Request

def query(table, *, active_seats=False):
    if not re.fullmatch(r"[1-9]\d{0,17}",table): raise ValueError("invalid table")
    delays=(0,) if active_seats else (0,1,3)
    for delay in delays:
        if delay:
            time.sleep(delay)
        try:
            return _query_once(table,active_seats=active_seats)
        except HTTPError as error:
            # 明确限速或权限拒绝交给审核人；临时网关故障只重试公开 GET。
            if error.code not in (408, 500, 502, 503, 504) or (error.headers or {}).get("Retry-After") or delay == delays[-1]:
                raise
        except (URLError, TimeoutError, ConnectionError):
            if delay == delays[-1]:
                raise


class _OfficialRedirect(HTTPRedirectHandler):
    def redirect_request(self,req,fp,code,msg,headers,newurl):
        dest=urlsplit(newurl)
        if (dest.scheme!="https" or dest.hostname!="boardgamearena.com" or dest.port not in (None,443)
            or dest.username or dest.password or dest.path not in ("/table","/table/table/tableinfos.html")):
            raise ValueError("原桌官方查询发生未核准的重定向")
        return super().redirect_request(req,fp,code,msg,headers,newurl)

def _query_once(table, *, active_seats=False):
    url="https://boardgamearena.com/table?table="+table
    handlers=[HTTPCookieProcessor(CookieJar())]
    if active_seats:
        handlers.extend([ProxyHandler({}),_OfficialRedirect()])
    session=build_opener(*handlers)
    timeout=7 if active_seats else 15
    with session.open(Request(url,headers={"Accept-Language":"en"}),timeout=timeout) as response:
        dest=response.geturl()
        site=urlsplit(dest)
        if (site.scheme!="https" or not (site.hostname=="boardgamearena.com" or site.hostname.endswith(".boardgamearena.com"))
            or site.path!="/table" or site.username or site.password):
            raise ValueError("需另行核对官方结算页")
        html=response.read().decode("utf8","replace")
    token=re.search(r"""(?:requestToken|request_token|csrfToken)["']?\s*[:=]\s*["']([^"']{8,})["']""",html)
    if not token: raise ValueError("公开结算页没有只读查询上下文")
    endpoint=urlunsplit((site.scheme,site.netloc,"/table/table/tableinfos.html","id="+table,""))
    with session.open(Request(endpoint,headers={"X-Requested-With":"XMLHttpRequest","Referer":dest,
                                               "X-Request-Token":token[1]}),timeout=timeout) as response:
        if active_seats:
            dest=urlsplit(response.geturl())
            if (dest.scheme!="https" or dest.hostname!="boardgamearena.com" or dest.port not in (None,443)
                or dest.path!="/table/table/tableinfos.html"):
                raise ValueError("无法独立核实官方原桌")
        raw=json.load(response)
    if str(raw.get("status"))!="1": raise ValueError("官方只读接口尚未返回桌信息")
    data=raw["data"]
    if str(data.get("id"))!=table or data.get("game_name")!="seasaltpaper": raise ValueError("官方回包桌号或游戏不符")
    game=data.get("result") or {}
    players=[]
    for raw_seat in game.get("player") or []:
        players.append(dict(id=str(raw_seat["player_id"]),name=raw_seat.get("name"),
            rank=int(raw_seat["gamerank"]) if raw_seat.get("gamerank") is not None else None,
            score=int(raw_seat["score"]) if raw_seat.get("score") is not None else None,
            isAi=raw_seat.get("is_ai")))
    result=dict(table=table,url=url,retrievedAt=datetime.now(timezone.utc).isoformat(),
                officialStatus=data.get("status"),cancelled=str(data.get("cancelled")),
                endReason=game.get("endgame_reason"),players=players,
                tableInfo={k:data.get(k) for k in ("gamestart","min_player","max_player","current_player_nbr",
                                                     "current_present_player_nbr")})
    if active_seats:
        seats=data.get("players")
        opts=data.get("options") or {}
        result["activeSeatIds"]=sorted(seats) if isinstance(seats,dict) else []
        result["game"]=data.get("game_name")
        result["options"]={str(i):(opts.get(str(i)) or {}).get("value") for i in (101,102,110,200,201)}
    return result

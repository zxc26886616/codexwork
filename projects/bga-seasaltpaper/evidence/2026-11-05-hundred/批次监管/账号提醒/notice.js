(function notice(mode, expected, readDialog) {
    "use strict";
    const u = new URL(location.href), origin = "https://boardgamearena.com";
    const account = window.globalUserInfos;
    const clean = s => (s || "").replace(/\s+/g, " ").trim();
    const norm = s => clean(s).replace(/\s+/g, "");
    const shown = e => !!e && !!e.getClientRects().length && getComputedStyle(e).visibility === "visible" &&
        !e.closest('[hidden],[aria-hidden="true"]') && getComputedStyle(e).opacity !== "0";
    const text = e => clean(e?.innerText), links = [...document.querySelectorAll("a[href]")].filter(shown);
    const enabled = e => !e.closest('[disabled],.disabled,[aria-disabled="true"]') && getComputedStyle(e).pointerEvents !== "none";
    const safe = s => clean(s).replace(/https?:\/\/\S+|[?&]\S+|[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi, "[省略]").slice(0,200);
    const buttons = [...document.querySelectorAll('button,a[href],[role=button]')].filter(shown);
    const actionLabels = buttons.map(text).filter(s => /^(明白了！|确认|关闭|继续|开始验证|验证身份|完成验证|返回游戏)$/.test(s));
    const lines = (document.body?.innerText || "").split(/\r?\n/).map(clean).filter(Boolean);
    const controls=buttons.filter(e=>actionLabels.includes(text(e))).slice(0,8).map(e=>{
        const href=e.tagName==="A"?new URL(e.href):null;
        return {text:text(e),path:href?.origin===origin?href.pathname:null,enabled:enabled(e)};
    });
    const out = {origin:u.origin, path:u.pathname, hostId:String(account?.id||""), account:account?.name||"",
        kind:"loading", eligible:false, controls, table:null, when:null};
    const result = (kind, hint) => ({...out,kind,hint});
    if (u.origin !== origin || u.username || u.password) return result("site","站点不符");
    if (!account?.id) return result("loading","登录身份尚未载入");
    if (out.hostId !== "99769903" || out.account !== "bke780") return result("identity","账号不符");
    const progress = [...document.querySelectorAll('[id*="gamesinprogress"],[id*="inprogress"],[id*="currentgame"],[id*="current_game"]')]
        .filter(shown).some(e => text(e));
    if (progress || [...document.querySelectorAll(".gamestart_overlay,.wannaplay_waiting")].some(shown) ||
        lines.some(s=>/返回游戏|继续游戏|重新加入对局|正在进行的对局|已经加入排队|轮到你行动/.test(s)))
        return result("table-or-queue","当前对局或匹配优先，保持原桌");
    const dialog=readDialog(mode,expected,out);
    if(dialog)return dialog;
    const security = lines.find(s=>/验证码|双重验证|身份验证|安全验证|通行密钥|人机验证|安全密钥|一次性(?:码|密码)|验证码设备/.test(s));
    if (security || [...document.querySelectorAll('input[autocomplete="one-time-code"],input[type=password],iframe[src*="captcha"]')].some(shown))
        return {...result("security","需核对官方验证入口，完成后新进程复查"),
            need:[...document.querySelectorAll('input[autocomplete="one-time-code"],input[type=password]')].some(shown)||
                /已发送|请输入|请在.{0,8}手机|扫码确认/.test(security||"")?"本人收到的一次性码、登录凭据或设备批准":null};
    const cooldown = lines.find(s=>/冷却|解禁|暂停匹配|禁止开始|暂时无法|无法开始|限制匹配|封禁|才能再次|(?:请等待|剩余).{0,16}(?:分钟|小时|秒)|cooldown|suspended until/i.test(s));
    if (cooldown) return {...result("cooldown","以平台给出的解禁时间为准，期限后另进程复核"),condition:safe(cooldown)};
    const modal=[...document.querySelectorAll('dialog,[role=dialog],[role=alertdialog],.dijitDialog,.modal,.standard_popin,.standard_popin_underlay,[aria-modal=true]')].find(shown);
    if(modal&&u.pathname!=="/penalty")return text(modal)?result("other","大厅显示其他对话框，按记录的官方入口复核"):
        result("loading","弹窗尚未显示内容，在同一页面继续只读观察");
    if(u.pathname==="/lobby"&&lines.some(s=>/未完成对局|惩罚|处罚|不能开始|禁止匹配|penalty|suspend|your turn|rejoin game/i.test(s)))
        return result("other","大厅还有平台限制或对局提示，需核对显示的原始页面");
    // 全局账号会先于大厅模块出现。在同一份文档中有界等待，期间仍优先报告真实弹窗、队列和对局。
    if(u.pathname==="/lobby"&&!shown(document.querySelector("#gamelobby-module")))
        return result("loading","账号已载入，等待大厅模块和游戏入口");
    if (u.pathname==="/lobby" && shown(document.querySelector("#gamelobby-module")) &&
        !lines.some(s=>/未能完成某一场游戏|名誉值惩罚/.test(s))){
        const mod=document.getElementById("gamelobby-module");
        const playable=["Xannaplay_1631","wannaplay_1631"].map(id=>document.getElementById(id)).find(e=>shown(e)&&enabled(e)&&
            /海盐折纸|Sea Salt/i.test(text(e))&&/开始游戏|Play now/i.test(text(e)));
        const regular=["rankingmode_simple","gamemode_realtime","opponentmode_automatic"].every(k=>mod.classList.contains(k));
        return {...result("idle","大厅已载入"),readySeaSalt:!!playable&&regular};
    }
    if (u.pathname==="/welcome"||u.pathname==="/")return result("welcome","已读取同账号官方欢迎页");
    if (u.pathname!=="/penalty") return result("other","其他页面，需要核对官方控件");
    const titles=[...document.querySelectorAll("h1,h2")].filter(shown);
    const title="糟糕，看来你未能完成某一场游戏:(";
    if (titles.length!==1 || norm(text(titles[0]))!==title) return result("other","其他提醒");
    const tables=links.filter(e=>{const t=new URL(e.href);return t.origin===origin&&t.pathname==="/table";});
    if(tables.length!==1 || text(tables[0])!=="海盐折纸")return result("other","未能唯一绑定海盐折纸历史桌");
    const url=new URL(tables[0].href), id=url.searchParams.get("table")||"";
    const date=norm(text(tables[0].parentElement)).match(/^海盐折纸\((\d{4})年([A-Za-z]{3}|\d{1,2}月)(\d{1,2})日(\d{1,2}):(\d{2})\)$/);
    if(!/^[1-9]\d{0,17}$/.test(id)||url.searchParams.getAll("table").length!==1||!date) return result("other","历史桌号或时间不完整");
    const months=["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];
    const month=months.includes(date[2])?months.indexOf(date[2])+1:Number(date[2].replace("月",""));
    const [year,day,hour,min]=[date[1],date[3],date[4],date[5]].map(Number);
    const stamp=new Date(Date.UTC(year,month-1,day,hour,min));
    if(month<1||month>12||stamp.getUTCMonth()!==month-1||day<1||hour>23||min>59||
        stamp.getTime()-8*3600000>Date.now()) return result("other","公告日期无效或尚未来到");
    out.table=id;out.when=stamp.toISOString().slice(0,16);
    const penalties=lines.map(norm).filter(s=>s.startsWith("名誉值惩罚")||s.startsWith("别担心：既然这是第一次"));
    const first="别担心：既然这是第一次，您的名誉不会受到任何惩罚（☯）。";
    const amount=penalties[0]?.match(/^名誉值惩罚[:：][-−]([1-9]\d*)☯（因为你近期又受到了另一项惩罚）$/);
    if(penalties.length!==1||!(penalties[0]===first||amount)||
        !lines.some(s=>norm(s)==="对每个人来说，未完成的对局视为败局。"))
        return result("other","名誉提示与已验证的普通通知不符");
    const labels=buttons.filter(e=>text(e)==="明白了！");
    const start=lines.findIndex(s=>norm(s)===title), stop=lines.indexOf("明白了！",start+1);
    const allowed=new Set([norm(text(tables[0].parentElement)),penalties[0],"对每个人来说，未完成的对局视为败局。","协助我们让玩家们开心！"]);
    if(start<0||stop<0||lines.slice(start+1,stop).some(s=>!allowed.has(norm(s))))
        return result("other","该提醒包含额外条件，需要另行核对");
    if(labels.length!==1||labels[0].tagName!=="A"||new URL(labels[0].href).href!==origin+"/welcome"||!enabled(labels[0])||
        labels[0].hasAttribute("download")||!["","_self"].includes(labels[0].target)||
        actionLabels.some(s=>s!=="明白了！")) return result("other","缺少唯一启用的原站确认控件");
    out.penalty=penalties[0]===first?"first":"deducted-"+amount[1];
    const state={...out,kind:"ordinary",eligible:true};
    if(mode==="inspect")return state;
    if(!expected||expected.table!==id||expected.when!==out.when||expected.penalty!==out.penalty) return result("changed","原提醒已变化");
    if(mode==="request") {
        const ms=window.mainsite;
        if(typeof ms?.ajaxcall!=="function")return result("unverified","该页未载入官方桌信息接口");
        const ref=window.__seaSaltNoticeRead={id,when:out.when,done:false};
        const valid=()=>window.__seaSaltNoticeRead===ref&&location.origin===origin&&location.pathname==="/penalty";
        try {
            ms.ajaxcall("/table/table/tableinfos.html",{id:Number(id)},ms,q=>{
                if(!valid())return;
                const seats=Object.keys(q?.players||{});
                ref.verified=String(q?.id)===id&&q.game_name==="seasaltpaper"&&["finished","archive"].includes(q.status)&&
                    seats.length===2&&seats.includes(out.hostId)&&seats.every(x=>/^[1-9]\d*$/.test(x));
                ref.status=String(q?.status||""); ref.seats=seats.filter(x=>/^[1-9]\d*$/.test(x)); ref.done=true;
            },err=>{if(err&&valid())ref.done=true;},"get",()=>{if(valid())ref.done=true;});
        }catch {ref.done=true;}
        return state;
    }
    const ref=window.__seaSaltNoticeRead;
    if(ref?.id!==id||ref.when!==out.when||!ref.done)return result("reading","正在核实历史桌");
    if(!ref.verified)return result("unverified","桌号、游戏、两个席位或已结束状态不符");
    const verified={...state,verified:true,officialStatus:ref.status,seats:ref.seats};
    if(mode!=="click")return verified;
    labels[0].click();
    return {...verified,dispatched:true,clickCount:1,target:"/welcome"};
})

(function accountDialog(mode,expected,base) {
    const norm=s=>(s||"").replace(/\s+/g," ").trim();
    const visible=e=>!!e && !!e.getClientRects().length && getComputedStyle(e).visibility==="visible" &&
        getComputedStyle(e).opacity!=="0" && !e.closest('[hidden],[aria-hidden="true"]');
    const safe=s=>norm(s).replace(/https?:\/\/\S+|[?&]\S+|[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi,"[省略]")
        .replace(/\b\d{4,8}\b/g,"[数字]").slice(0,180);
    const official=location.origin==="https://boardgamearena.com";
    const dialogs=[...document.querySelectorAll('dialog,[role=dialog],[role=alertdialog],.dijitDialog,.modal,.standard_popin,[aria-modal=true]')].filter(visible);
    if(dialogs.length>1)return {...base,kind:"other",hint:"同时存在多个弹窗"};
    const securityPath=/^\/(?:account\/)?(?:security|verify|verification|challenge|twofactor)(?:\/(?:start|continue|verify|check|device|code))?\/?$/i;
    const route=securityPath.test(location.pathname)||location.pathname==="/penalty";
    const scope=dialogs[0]||(route?document.querySelector('main,[role=main],#main-content')||document.body:null);
    if(!official||!scope)return null;
    const headings=[...scope.querySelectorAll("h1,h2,h3,[role=heading],.dijitDialogTitle")].filter(visible);
    const title=norm(headings[0]?.innerText);
    if(!title||title.includes("未能完成某一场游戏"))return null;
    const body=norm(scope.innerText);
    const controls=[...scope.querySelectorAll('button,a[href],[role=button],input[type=button],input[type=submit]')].filter(visible);
    const label=e=>norm(e.innerText||e.value||e.getAttribute("aria-label")).replace(/[！!。]$/,"");
    const blocked=s=>/删除|注销|切换账号|更换账号|退出账号|更改密码|购买|支付|订单|订阅|兑换|授权|绑定新|转让|同意条款|入队|加入匹配|开始游戏|建立牌桌|重新开局|报名|认输|惩罚申诉/.test(s);
    const entries=controls.map(e=>{const u=e.tagName==="A"?new URL(e.href):null;
        return {text:safe(label(e)),path:u?.origin===location.origin?u.pathname:null,
            enabled:!e.closest('[disabled],.disabled,[aria-disabled=true]')&&getComputedStyle(e).pointerEvents!=="none"};});
    const result=(kind,hint)=>({...base,kind,title:safe(title),context:safe(body),controls:entries,hint,eligible:false});
    if(blocked(body))return result("other","弹窗涉及账号、交易或牌桌业务");
    if(securityPath.test(location.pathname)&&/^(身份|安全)?验证(成功|已完成|完成)$/.test(title)&&
        /(?:已完成验证|验证成功|验证已通过)/.test(body.slice(title.length))&&!controls.length)
        return result("security-complete","官方验证页明确显示验证完成");
    const cool=/冷却|匹配暂停|暂停匹配|暂时禁止匹配|暂时无法匹配/.test(title);
    const auth=/安全验证|身份验证|安全检查|人机验证|双重验证/.test(title);
    const simple=/^(系统|平台|服务|大厅)(公告|通知|提示)$/.test(title);
    if(!cool&&!auth&&!simple)return result("other","弹窗用途未经核实");
    if(simple && (!/公告|通知|欢迎|维护结束|恢复正常|更新完成|提示/.test(body.slice(title.length))||
        /风险|处罚|警告|名誉|惩罚|验证|承诺|协议|条款|确认是否|是否要|游戏进行中/.test(body)))return result("other","公告内容不能确认为单纯提示");
    const kind=cool?"cooldown":auth?"security":"ordinary-dialog";
    const res=result(kind,cool?"按官方期限解除后复查":auth?"只进入官方验证流程":"确认非牌桌普通通知");
    if(auth){
        const entry=controls.some(e=>["开始验证","继续"].includes(label(e)));
        if([...scope.querySelectorAll('input[autocomplete="one-time-code"],input[name*=otp],input[type=password]')].some(visible)||
            !entry&&/(?:输入|提交|填写|请查收|已发送).{0,18}(验证码|一次性码|一次性密码)/.test(body)) {
            res.need="本人收到的一次性码或本人登录凭据，请在官方验证页面提供";return res;
        }
        if(!entry&&/(?:使用|请在|需要|批准|确认).{0,18}(本人设备|手机|安全密钥|验证器)|在手机上.{0,12}(确认|批准)|扫码确认/.test(body)){
            res.need="本人设备上的批准、扫码或安全密钥";return res;
        }
    }
    let timeKey="";
    if(cool){
        // 仅采用页面实际发布的明确时区绝对时间，或明确带单位的剩余时间，并原样保留证据。
        const semantic=[...scope.querySelectorAll('time[datetime]')].filter(visible).map(e=>e.dateTime);
        const clocks=body.match(/\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d\d:\d\d)\b/g)||[];
        const dates=[...new Set([...semantic,...clocks])].filter(s=>/^(?:\d{4}-\d{2}-\d{2}T\d\d:\d\d(?::\d\d(?:\.\d+)?)?)(?:Z|[+-]\d\d:\d\d)$/.test(s));
        const chinese=body.match(/(?:解禁|解除|截止|结束)(?:时间)?[：:\s]*(\d{4})[-年](\d{1,2})[-月](\d{1,2})日?\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(?:北京时间|UTC\+8)/);
        if(chinese)dates.push(chinese[1]+"-"+chinese[2].padStart(2,"0")+"-"+chinese[3].padStart(2,"0")+"T"+chinese[4].padStart(2,"0")+":"+chinese[5]+":"+(chinese[6]||"00")+"+08:00");
        const instants=[...new Set(dates.map(Date.parse).filter(Number.isFinite))];
        const remain=body.match(/(?:剩余(?:时间)?|还需(?:等待)?|请等待)[：:\s]*(?:(\d+)\s*小时)?\s*(?:(\d+)\s*分钟)?\s*(?:(\d+)\s*秒)?/);
        if(remain&&(remain[1]||remain[2]||remain[3]))res.remainingSeconds=Number(remain[1]||0)*3600+Number(remain[2]||0)*60+Number(remain[3]||0);
        if(instants.length>1||(!instants.length&&!Number.isSafeInteger(res.remainingSeconds))){res.hint="未获得唯一官方截止时间或可核实的剩余秒数";return res;}
        if(instants.length)res.untilUtc=new Date(instants[0]).toISOString();
        res.serverWording=dates.join(" / ")||(remain&&remain[0]);
        timeKey=res.untilUtc||"relative";
    }
    const names=auth?["开始验证","继续"]:cool?["知道了","我知道了","明白了","确认","关闭"]:["知道了","我知道了","明白了","确认","继续","关闭"];
    const pick=controls.filter(e=>names.includes(label(e)));
    if(controls.length!==1||pick.length!==1||!entries[0].enabled)return res;
    const el=pick[0], href=el.tagName==="A"?new URL(el.href):null;
    if(el.closest("form")||el.type==="submit"&&el.tagName==="INPUT"||el.hasAttribute("download")||
        el.target&&el.target!=="_self"||href&&(href.origin!==location.origin||href.username||href.password||
        !(["/","/lobby","/welcome","/penalty"].includes(href.pathname)||auth&&securityPath.test(href.pathname))||
        (href.hash&&href.hash!=="#")||
        href.search&&!(auth&&securityPath.test(href.pathname)&&[...href.searchParams.keys()].every(k=>["state","challenge","id","lang"].includes(k)))))return res;
    const stableBody=cool?body.replace(/(?:剩余(?:时间)?|还需(?:等待)?|请等待)[：:\s]*(?:\d+\s*(?:小时|分钟|秒)\s*)+/g,"[剩余时间]"):body;
    const key=[base.hostId,kind,location.pathname,scope.id,title,stableBody,timeKey,label(el),href?.pathname||""].join("|");
    Object.assign(res,{eligible:true,key,actionLabel:label(el),targetPath:href?.pathname||null});
    if(mode!=="click")return res;
    if(!expected||expected.key!==key||expected.kind!==kind)return {...res,eligible:false,dispatched:false,hint:"弹窗或目标发生变化"};
    el.click();return {...res,dispatched:true,clickCount:1};
})

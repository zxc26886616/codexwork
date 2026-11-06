(function lobbySnapshot() {
    const visible=e=>!!e&&e.getClientRects().length>0&&!e.closest('[hidden],[aria-hidden=true]')&&getComputedStyle(e).visibility!=="hidden"&&getComputedStyle(e).opacity!=="0";
    const clean=s=>(s||"").replace(/https?:\/\/\S+|[?&]\S+|[\w.+-]+@[\w.-]+\.\w+/g,"[省略]").replace(/\s+/g," ").slice(0,160);
    const text=e=>clean(e?.innerText||e?.textContent);
    const all=q=>[...document.querySelectorAll(q)].filter(visible);
    const mod=document.getElementById("gamelobby-module");
    const ids=["Xannaplay_1631","wannaplay_1631","pageheader_realtime","pageheader_simple","pageheader_automatic"];
    const controls=ids.map(id=>document.getElementById(id)).filter(visible).map(e=>({id:e.id,text:text(e),disabled:!!e.closest(':disabled,.disabled,[aria-disabled=true]')||getComputedStyle(e).pointerEvents==="none"}));
    const headings=all("h1,h2,h3,[role=heading]").map(text).filter(Boolean).slice(0,18);
    const modalNodes=all('dialog[open],[role=dialog],[role=alertdialog],.dijitDialog,.dijitDialogUnderlay,.modal,.standard_popin,.standard_popin_underlay,[aria-modal=true]');
    const labels=all('button,a[href],[role=button],input[type=button],input[type=submit]').map(e=>text(e)||clean(e.value||e.getAttribute("aria-label")));
    const lines=(document.body?.innerText||"").split(/\r?\n/).filter(s=>/未能完成|未完成对局|名誉值惩罚|处罚提醒|冷却|暂停匹配|不能开始|无法开始|正在进行的对局|轮到你|返回游戏|继续游戏|明白了|验证码|安全验证|解禁|设备批准|suspend|your turn|rejoin game|penalty|cooldown/i.test(s)).map(clean);
    return {origin:location.origin,path:location.pathname,user:window.globalUserInfos?{id:String(window.globalUserInfos.id),name:String(window.globalUserInfos.name)}:null,
        lobby:mod?{visible:visible(mod),modes:["rankingmode_simple","gamemode_realtime","opponentmode_automatic"].filter(x=>mod.classList.contains(x))}:null,
        controls,headings,noticeLines:lines.slice(0,25),noticeTables:location.pathname==="/penalty"?all('a[href*="/table?table="]').map(e=>({text:text(e)})):[],
        progress:all('[id*="gamesinprogress"],[id*="inprogress"],[id*="currentgame"],[id*="current_game"]').map(e=>({id:e.id,text:text(e)})),
        queuedIds:all(".wannaplay_waiting").map(e=>e.id),overlays:all(".gamestart_overlay").map(e=>e.id),
        dialogs:modalNodes.map(e=>({role:e.getAttribute("role"),text:text(e)})), noticeControls:labels.filter(s=>/^(知道了|我知道了|明白了|确认|继续|关闭|开始验证|验证身份|返回游戏)[！!。]?$/.test(s)),
        credentials:all('input[type=password],input[autocomplete=one-time-code],iframe[src*="captcha"]').length};
})

function operateLobby(op, arg) {
    "use strict";
    const result = (ok, message) => ({ ok, message });
    const url = new URL(location.href);
    if (url.protocol !== "https:" || !/(^|\.)boardgamearena\.com$/.test(url.hostname))
        return result(false, "此页面不支持后台开桌命令。");
    // 处罚提醒会在大厅操作后整页跳转。只报告固定路径，查询参数可能包含账号信息。
    if (url.pathname === "/penalty" && (op === "realtime" || op === "realtime-click"))
        return {ok:false,kind:"blocked",code:"platform-penalty",origin:url.origin,path:"/penalty",
            message:"平台打开了处罚提醒页，请核实页面提示（" + url.origin + "/penalty）。"};
    if (!/^\/(?:lobby|gamepanel|table|tableview|(?:\d+\/)?seasaltpaper)\/?$/.test(url.pathname))
        return result(false, "此页面不支持后台开桌命令。");
    const norm = text => String(text || "").replace(/\s+/g, " ").trim();
    const realtimeAtTable = (w, table) => {
        const game = w?.gameui;
        if (game?.game_name !== "seasaltpaper" || String(game.table_id) !== table || !game.gamedatas?.gamestate) return null;
        const current = w.bgaGameData?.isRealTime, legacy = game.bRealtime;
        if (typeof current === "boolean" && typeof legacy === "boolean" && current !== legacy) return null;
        return typeof current === "boolean" ? current : typeof legacy === "boolean" ? legacy : null;
    };
    const visible = e => !!e?.isConnected && e.getClientRects().length > 0 &&
        !e.closest('[inert],[aria-hidden="true"]') && e.checkVisibility({opacityProperty:true, visibilityProperty:true});
    const systemInput = 'input:is([type="password"],[type="file"],[type="color"])';
    const forbidden = e => !!e.closest(systemInput + ',[contenteditable="true"],#chatbar,#chatwindow,#overall-footer,[class*="chatwindow"]') ||
        !!e.closest("label")?.control?.matches(systemInput) ||
        /password|token|captcha|checkout|payment|creditcard/i.test([e.id,e.name,e.autocomplete].join(" "));
    const label = e => norm((e.tagName === "SELECT" ? e.selectedOptions[0]?.textContent : e.innerText) ||
        e.getAttribute("aria-label") || e.getAttribute("title") || e.getAttribute("placeholder") || e.getAttribute("alt"));
    const trophySelector = '.splash_block[id^="splash_trophy_"]';
    function trophyNotice() {
        const splash = [...document.querySelectorAll(trophySelector)].reverse().find(visible);
        const el = splash?.querySelector('[id^="continue_btn_"]');
        const dismiss = visible(el) && /^(继续|繼續|continue)$/i.test(norm(el.innerText)) ? el : null;
        return {splash,dismiss};
    }
    const realtimeClick = op === "realtime-click";
    const query = arg.trim().toLocaleLowerCase();
    if (op === "series-info" || (op === "inspect" && arg === "连局")) {
        const byId = id => document.getElementById(id);
        const shown = id => visible(byId(id));
        const table = url.searchParams.get("table");
        const user = String(window.globalUserInfos?.id ?? "");
        if (!/^[1-9]\d*$/.test(user)) return result(false, "等待已登录的 BGA 页面。");
        if (url.pathname === "/lobby") {
            const requestedAsync = arg === "turn-based";
            const speedItem = requestedAsync ? "pageheader_async" : "pageheader_realtime";
            const module = byId("gamelobby-module");
            const clockMode = module?.classList.contains("gamemode_async") ? "async" :
                module?.classList.contains("gamemode_realtime") ? "realtime" : "";
            const rows = Array.from(document.querySelectorAll("#lobby_invited_friend_list .lobby_invite_friend_item_player"));
            const row = rows[0], title = byId("lobby_invited_friend_section_title");
            const name = norm(row?.querySelector(".lobby_invite_friend_name")?.textContent);
            const memberId = row?.id.replace(/^gamesession_player_/, "") || "";
            const member = window.gaming_session?.members?.[memberId];
            const {splash,dismiss} = trophyNotice();
            // 引导演示区会复制真实菜单项的 ID。先展开当前步骤的顶部菜单，再选真实选项。
            const step = [
                ["rankingmode_explanation","pageheader_mobile_switcher_gamemode","pageheader_simple"],
                ["gamemode_explanation","pageheader_mobile_switcher",speedItem],
                ["opponentmode_explanation","pageheader_mobile_switcher_lobbymode","pageheader_friends"]
            ].find(([panel]) => shown(panel));
            const inMenu = id => visible(document.querySelector("#pageheader_lobbymenunav_wrap #" + id));
            const wizard = step && (inMenu(step[2]) ? step[2] : step[1]);
            const wrongSpeed = clockMode && clockMode !== (requestedAsync ? "async" : "realtime");
            const localMember = member?.status === "hotseat" || member?.player_type === "hotseat" ||
                row?.classList.contains("lobby_invite_friend_item_hotseat");
            return {ok:true, page:"lobby", host:user, clockMode, partners:rows.length, partner:name, memberId, localMember:!!localMember,
                ...(op === "inspect" ? {状态:member?.status, 引导:step?.[0], 视口宽度:innerWidth} : {}),
                confirmed:rows.length === 1 && visible(row) && visible(title) && norm(title.innerText).includes(name) &&
                    !byId("lobby_invite_friend_zone")?.classList.contains("passive_guest_on_gamesession"),
                splash:!!splash, dismiss:dismiss?.id || "",
                wizard:wizard || (wrongSpeed ? (inMenu(speedItem) ? speedItem : "pageheader_mobile_switcher") : ""),
                nickname:shown("hotseat_player_name") ? byId("hotseat_player_name").value : null,
                nearby:shown("friend_nearby_button"), addFriend:shown("lobby_add_friend_button"),
                expand:shown("playwithfriend_link") && norm(byId("playwithfriends_icon")?.textContent || byId("playwithfriend_link").innerText).startsWith("+"),
                tutorial:shown("newArchiveCommentNext"), tutorialText:norm(byId("currentTutorialDialog")?.innerText).slice(0,180),
                play:shown("Xutton_play_1631")};
        }
        if (!/^[1-9]\d*$/.test(table || "")) return result(false, "等待有效桌号。");
        if (url.pathname === "/tableview") return {ok:true, page:"game", table, host:user};
        if (url.pathname !== "/table") return result(false, "当前页面不是大厅或游戏桌。");
        if (!document.querySelector(".tablewithgame_seasaltpaper") &&
            !/game=seasaltpaper(?:&|$)/.test(byId("table_name")?.search || "")) return result(false, "正在核对海盐折纸桌面。");
        const rows = Array.from(document.querySelectorAll("#players_at_table .active_player"));
        const guests = rows.filter(el => el.classList.contains("hotseat_player"));
        const hostRow = rows.find(el => !el.classList.contains("hotseat_player") && el.id === "active_player_" + user);
        const adminLabels = ["房主","桌主","table administrator"];
        if (typeof window._ === "function") adminLabels.push(norm(window._("table administrator")));
        const isHost = [...(hostRow?.querySelectorAll(".active_player_action_block .smalltext") || [])]
            .some(el => adminLabels.some(s => s && norm(el.textContent).toLowerCase().includes("(" + s.toLowerCase() + ")")));
        const options = [201,101,102,110].map(id => {
            const inputs = ["gameoption_", "mob_gameoption_"].map(prefix => byId(prefix + id + "_input"));
            const field = inputs.find(el => visible(el)) || inputs.find(el => el?.tagName === "SELECT");
            const values = ["gameoption_", "mob_gameoption_"].map(prefix => byId(prefix + id + "_displayed_value"));
            const displayed = norm(values.find(el => visible(el))?.textContent);
            const expected = id === 201 ? /^(友谊赛模式|友谊赛|友誼賽模式|friendly mode)$/i : /^(禁用|停用|关闭|關閉|已禁用|disabled|off)$/i;
            return {id:field?.id || "", value:field?.value || (expected.test(displayed) ? "1" : ""), editable:!!field && visible(field) && !field.disabled};
        });
        // 满席时 BGA 隐藏“对其他玩家公开”按钮，服务器的 init/open 仍反映在桌面状态类上。
        const stateClass = [...(byId("gametable_box")?.classList || [])].find(c => c.startsWith("gametable_status_"));
        const tableState = stateClass?.slice("gametable_status_".length) || "";
        const speedFields = ["gameoption_200_input","mob_gameoption_200_input"].map(byId);
        const rawSpeed = (speedFields.find(visible) || speedFields.find(el => el?.tagName === "SELECT"))?.value;
        const speed = /^\d+$/.test(rawSpeed || "") && Number(rawSpeed) > 0 ? Number(rawSpeed) : null;
        const stateSpeed = ["asyncinit","asyncopen","asyncplay"].includes(tableState) ? true :
            ["init","open","play"].includes(tableState) ? false : null;
        // 官方游戏速度选项 200 从 10 起表示回合制。已同步的桌面状态与控件不一致时先等待。
        const turnBased = speed == null ? stateSpeed : stateSpeed != null && stateSpeed !== (speed >= 10) ? null : speed >= 10;
        return {ok:true, page:"table", table, host:user, tableState, speed, turnBased, ended:shown("game_result") || shown("open_table_final_situation"),
            seats:rows.length, localSeats:guests.length, mySeat:!!hostRow, isHost, guest:norm(guests[0]?.querySelector("img.emblem")?.alt),
            privateTable:tableState ? ["init","asyncinit"].includes(tableState) : shown("open_table_now"), start:shown("startgame"),
            enter:shown("access_game_normal") && !byId("access_game_normal").matches(":disabled,.disabled,.bgabutton_disabled,[aria-disabled=true]"), options};
    }
    const snapshot = window.__seaSaltDeskControls ??= { seq:0, nodes:new Map() };
    if (op === "realtime" || op === "realtime-click") {
        const host = String(window.globalUserInfos?.id || window.gameui?.player_id || "");
        const table = url.searchParams.get("table");
        const validId = id => /^[1-9]\d{0,17}$/.test(id || "");
        let assigned = null;
        if (op === "realtime" && arg.startsWith("{")) {
            try { assigned = JSON.parse(arg); } catch { /* 非绑定观察仍沿用现有页面核对。 */ }
        }
        const state = (kind, message, el, number, opponent) =>
            ({ kind, message, host, action:el?.id, table:number, opponent });
        const wait = msg => state("wait", msg);
        const blocked = msg => state("blocked", msg);
        const control = (parent, id) => [...parent.querySelectorAll('[id="' + id + '"]')]
            .find(el => visible(el) && !el.closest("#beginner_lobby_explanations_panels") &&
                !el.matches(":disabled,.disabled,[aria-disabled=true]"));
        function observe() {
            const view = url.pathname === "/tableview" || /\/seasaltpaper\/?$/.test(url.pathname);
            if (view) {
                const frame = document.querySelector("#gameIframe");
                const w = url.pathname === "/tableview" ? frame?.contentWindow : window;
                const loading = (message, initializing = false) => validId(table) && assigned?.table === table && assigned.attached === false
                    ? {...state("wait", message, null, table), code:initializing ? "game-initializing" : "game-shell-empty"}
                    : wait(message);
                let game;
                try {
                    if (!w || w.location.href === "about:blank") return loading("正在核对分桌页面。", true);
                    if (w.location.origin !== url.origin || new URL(w.location.href).searchParams.get("table") !== table)
                        return wait("正在核对分桌页面。");
                    game = w.gameui;
                } catch { return wait("正在等待游戏框加载。"); }
                if (w.navigator.onLine === false || visible(w.document.getElementById("connect_gs_status")))
                    return loading("游戏服务器连接已断开，等待原桌恢复。", true);
                if (!validId(table) || !game?.gamedatas?.players)
                    return loading("等待即时制牌桌载入。", w.document.readyState !== "complete" || !!game?.gamedatas || game?.game_name === "seasaltpaper");
                const self = String(game.player_id), ids = Object.keys(game.gamedatas.players);
                const other = ids.find(id => id !== self);
                if (game.game_name !== "seasaltpaper" || String(game.table_id) !== table ||
                    game.isSpectator || w.g_archive_mode || (game.hotseatplayers?.length ?? 0) > 0 ||
                    ids.length !== 2 || !ids.includes(self) || !validId(other) || (host && self !== host) ||
                    game.gamedatas.extraSaltExpansion || game.gamedatas.extraPepperExpansion ||
                    game.gamedatas.doublePoints || game.players_metadata?.[other]?.type === "hotseat")
                    return blocked("分配的牌桌、座位或规则与双人基础版不符。");
                const realtime = realtimeAtTable(w, table);
                if (realtime === null) return loading("等候牌桌确认是否为即时制。", true);
                if (!realtime) return blocked("平台分配的桌不是即时制。");
                const myTurn = game.isCurrentPlayerActive?.() ?? game.bga?.players?.isCurrentPlayerActive?.();
                return { ...state("game", "已核对普通桌双方座位。", null, table, other), host:self,
                    myTurn:typeof myTurn === "boolean" ? myTurn : null, phase:game.gamedatas.gamestate?.name };
            }
            if (!validId(host)) return wait("等待确认当前登录账号。");
            // 已分配原桌须读新的官方回包。acceptGameStart.tableinfos 只是初始快照，遮罩与DOM都可能滞后。
            if (url.pathname === "/table" && validId(assigned?.table)) {
                if (table !== assigned.table || host !== assigned.host) return blocked("原桌页面或账号与已绑定匹配不符。");
                const pending = (message, code) => ({ ...state("allocated-wait", message, null, table, assigned.opponent), code });
                if (typeof window.mainsite?.ajaxcall !== "function")
                    return pending("等待原桌的官方只读接口。", "allocated-reader-missing");
                let check = snapshot.allocatedCheck;
                const now = Date.now();
                // 长等待后原 offer 已停续排，只加快最新一次确认 ready 的成功回包复查；失败仍用常规间隔。
                const urgentReady = assigned.heldOffer === true && !check?.error && check?.data?.status === "ready";
                if (!check || check.table !== table || check.done && now - check.finished >= (assigned.emergency === true || urgentReady ? 1000 : 4000)) {
                    check = snapshot.allocatedCheck = {table, url:location.href, started:now, done:false, expired:false};
                    const current = () => snapshot.allocatedCheck === check && !check.expired && location.href === check.url;
                    const fail = () => { if (current()) { check.done = true; check.error = true; check.finished = Date.now(); } };
                    try {
                        window.mainsite.ajaxcall("/table/table/tableinfos.html", {id:Number(table)}, window.mainsite, q => {
                            if (!current()) return;
                            check.data = q; check.done = true; check.finished = Date.now();
                        }, error => { if (error) fail(); }, "get", fail);
                    } catch { fail(); }
                }
                // 未进游戏也可能已开始计时。短预算到期只废弃本读，由宿主重载原桌取消旧文档后再查。
                if (!check.done && now - check.started >= 5000) {
                    check.expired = true;
                    return pending("官方原桌读取未返回，重新核对同一桌。", "allocated-read-timeout");
                }
                if (!check.done || check.error) return pending(check.error ?
                    "官方原桌读取失败，稍后只读重试。" : "正在读取原桌最新官方状态。", "allocated-reading");
                const q = check.data;
                if (!q || String(q.id) !== table || q.game_name !== "seasaltpaper")
                    return blocked("官方回包的桌号或游戏与已绑定匹配不符。");
                const status = String(q.status || "");
                const official = (kind, message) => ({ ...state(kind, message, null, table, assigned.opponent),
                    officialStatus:status, authorityAt:String(check.finished), code:"allocated-authority" });
                const ids = Object.keys(q.players || {});
                const sameSeats = ids.length === 2 && ids.includes(assigned.host) && ids.includes(assigned.opponent);
                if (status === "finished" || status === "archive") {
                    const reason = String(q.result?.endgame_reason || "").slice(0,64), cancelled = String(q.cancelled ?? "");
                    if ((assigned.attached === true || cancelled === "0" && reason === "normal_end") && !sameSeats)
                        return blocked("官方终局的双方席位与已绑定匹配不符。");
                    return { ...official(cancelled === "1" || reason.includes("skipturn") ? "void" :
                        cancelled === "0" && reason === "normal_end" ? "result" : "non-normal",
                        "官方已确认原匹配桌结束，不再进入游戏。"), endReason:reason, cancelled };
                }
                if (!sameSeats ||
                    ![101,102,110].every(i => String(q.options?.[i]?.value) === "1") ||
                    !["0","1","2"].includes(String(q.options?.[200]?.value)) ||
                    !["0","1"].includes(String(q.options?.[201]?.value)) || status.startsWith("async"))
                    return blocked("官方原桌的双方席位、速度或扩展与已绑定匹配不符。");
                if (assigned.attached === true && status === "play" &&
                    (control(document,"game_result") || document.querySelector("#gametable_box.gametable_status_finished")))
                    return official("allocated-wait", "原桌已显示结算，等候最新官方终局。");
                return official(status === "play" ? "started" : "allocated-wait",
                    status === "play" ? "官方原桌已开局，正在进入已绑定游戏。" : "官方原桌尚未确认开局，保留已分配桌继续核对。");
            }
            const overlay = [...document.querySelectorAll(".gamestart_overlay")].find(visible);
            if (overlay) {
                const offer = window.mainsite?.acceptGameStart?.tableinfos;
                if (!offer || overlay.id !== "gamestart_" + offer.id) return wait("等待完整的匹配确认。");
                const number = String(offer.id), seats = Object.keys(offer.players || {});
                const other = seats.find(id => id !== host);
                if (!validId(number) || offer.game_name !== "seasaltpaper" || seats.length !== 2 ||
                    !validId(other) || !seats.includes(host) || ![101,102,110].every(i => String(offer.options?.[i]?.value) === "1") ||
                    !["0","1","2"].includes(String(offer.options?.[200]?.value)) ||
                    !["0","1"].includes(String(offer.options?.[201]?.value)))
                    return blocked("匹配确认的游戏、人数、速度或扩展不符合双人基础版。");
                const accept = control(overlay,"ags_start_game_accept");
                if (accept?.classList.contains("bgabutton_darkgray")) return blocked("平台要求先确认桌上的额外提示。");
                if (accept) return state("offer","确认平台分配的双人桌。",accept,number,other);
                if (!control(overlay,"ags_start_game_status_wrap") && !overlay.classList.contains("game_about_to_start") &&
                    String(offer.players[host]?.decision) !== "1") return wait("等候平台显示己方匹配确认。");
                return state("confirming","己方已确认，等待对手或平台开局。",null,number,other);
            }
            if (url.pathname === "/table" && validId(table)) {
                if (!document.querySelector(".tablewithgame_seasaltpaper")) return wait("等待读取分配的游戏。");
                const ended = control(document,"game_result");
                if (ended) {
                    if (!snapshot.finalCheck || snapshot.finalCheck.table !== table) {
                        if (typeof window.mainsite?.ajaxcall !== "function") return wait("等候官方终局原因。");
                        const check = snapshot.finalCheck = {table, done:false};
                        // 结算排名也可能出现在取消、认输或未知原因的桌上；计数只认桌号一致的官方正常终局。
                        window.mainsite.ajaxcall("/table/table/tableinfos.html",{id:Number(table)},window.mainsite,q=>{
                            if (String(q?.id) !== table || q.game_name !== "seasaltpaper" ||
                                !["finished","archive"].includes(q.status)) return;
                            check.status = q.status;
                            check.reason = String(q.result?.endgame_reason || "").slice(0,64);
                            check.cancelled = String(q.cancelled ?? "");
                            check.done = true;
                        });
                    }
                    if (!snapshot.finalCheck.done) return wait("等候官方终局原因。");
                    const {reason,cancelled,status} = snapshot.finalCheck;
                    const final = (kind,message) => ({ ...state(kind,message,null,table), endReason:reason, cancelled, officialStatus:status });
                    if (cancelled === "1" || reason.includes("skipturn"))
                        return final("void","官方终局属于取消或跳过回合，未计入完赛。");
                    if (cancelled !== "0" || reason !== "normal_end")
                        return final("non-normal","官方未确认标准正常终局，已停止本批次续排。");
                    return final("result", "等候官方结算入库。");
                }
                const playing = document.querySelector("#gametable_box.gametable_status_play");
                const rows = [...document.querySelectorAll("#players_at_table .active_player")];
                const ids = rows.map(row => row.id.replace(/^active_player_/, ""));
                const enter = control(document, "access_game_normal");
                if (playing && (!enter || rows.length === 0)) return state("started","官方桌页已确认即时制开局。",null,table);
                if (!enter) return state("allocated-wait","等待分配的牌桌开放。",null,table);
                if (rows.length === 0) return wait("正在读取分配牌桌的座位。");
                if (rows.length !== 2 || !ids.includes(host) || ids.some(id => !validId(id)) ||
                    rows.some(row => row.classList.contains("hotseat_player")))
                    return blocked("分桌信息中的参与者不符合双人普通桌。");
                return state("enter", "进入平台分配的牌桌。", enter, table, ids.find(id => id !== host));
            }
            const lobby = document.getElementById("gamelobby-module");
            if (!lobby || url.pathname !== "/lobby") return wait("等待开始游戏大厅。");
            const {splash,dismiss} = trophyNotice();
            if (splash) return dismiss ? state("dismiss","关闭结算后的奖杯提示，继续匹配。",dismiss) : wait("等待奖杯提示显示继续按钮。");
            if (visible(document.getElementById("splashedNotifications_overlay"))) return wait("等待大厅收起奖杯遮罩。");
            if (document.querySelector(".wannaplay_waiting:not(#Xannaplay_1631):not(#wannaplay_1631)"))
                return blocked("大厅已有其他游戏正在排队。");
            const queued = lobby.querySelector("#Xannaplay_1631.wannaplay_waiting,#wannaplay_1631.wannaplay_waiting");
            if (queued) return state("queued", "即时制已入队，等待平台分桌。");
            const modes = [
                ["rankingmode_simple","pageheader_simple","pageheader_mobile_switcher_gamemode"],
                ["gamemode_realtime","pageheader_realtime","pageheader_mobile_switcher"],
                ["opponentmode_automatic","pageheader_automatic","pageheader_mobile_switcher_lobbymode"]
            ];
            for (const [mode, id, menu] of modes) {
                if (lobby.classList.contains(mode)) continue;
                const button = control(document,id) || control(document,menu);
                return button ? state("mode","切换大厅至普通游戏、即时制、自动对手。",button) : wait("等待大厅显示匹配模式选项。");
            }
            const prefs = document.getElementById("game_preferences_contents");
            if (!visible(prefs)) {
                const gear = ["Xannaplay_pref_1631","wannaplay_pref_1631"].map(id => control(lobby,id)).find(Boolean);
                return gear ? state("mode","核对海盐折纸的即时制匹配偏好。",gear) : wait("等待大厅显示海盐折纸的匹配设置。");
            }
            if (!snapshot.realtimePrefs) return blocked("请关闭其他游戏的偏好窗口，再开始即时制批次。");
            const players = prefs.querySelector("#mm_prefs_wrapper_299");
            if (!players) return blocked("无法核对匹配的人数偏好。");
            const choices = [...players.querySelectorAll(".mm_pref_value")];
            const two = choices.find(el => el.id === "mm_pref_299_value_2");
            if (!two) return blocked("大厅没有可确认的双人匹配选项。");
            if (snapshot.preferenceChange) {
                const {id, priority} = snapshot.preferenceChange;
                if (!document.getElementById(id)?.classList.contains("mm_pref_color_priority_"+priority))
                    return { ...state("pref-saving", "等待大厅确认匹配偏好已保存。"), preference:id };
                snapshot.preferenceChange = null;
            }
            const required = [[two,true], ...choices.filter(el => el !== two).map(el => [el,false])];
            for (const id of [101,102,110]) {
                const off = prefs.querySelector("#mm_pref_" + id + "_value_1");
                const on = prefs.querySelector("#mm_pref_" + id + "_value_2");
                if (!off || !on) return blocked("无法核对基础版的扩展与双倍分数偏好。");
                required.push([off,true],[on,false]);
            }
            for (const [el, allow] of required) {
                const priority = [...el.classList].find(c => c.startsWith("mm_pref_color_priority_"));
                if (!priority) return wait("等待偏好值载入。");
                if (allow ? /_[01]$/.test(priority) : priority.endsWith("_9")) continue;
                const group = el.closest(".mm_prefs_wrapper");
                const select = control(prefs,el.id+"_priority_"+(allow ? "1" : "9"));
                const option = select || control(prefs,el.id) || control(prefs,group.id.replace("mm_prefs_wrapper_","mm_change_pref_")) ||
                    control(prefs,"toggle_advanced_link");
                return option ? state("mode","设置双人基础版的匹配偏好。",option) : wait("等待基础版匹配偏好可用。");
            }
            const play = control(prefs,"play_now");
            return play ? state("join","开始海盐折纸双人基础版即时制匹配。",play) : wait("等待可入队的开始按钮。");
        }
        const current = observe();
        if (op === "realtime") return current;
        // 在同一页面调用中复核账号、桌号、对手和控件，再走已有的可见性和遮挡检查。
        const key = [current.kind,current.host,current.table,current.opponent,current.action].join("|");
        // 页面可以在两次 WebView 调用之间更新。此处尚未派发任何点击，可重新观察再决定。
        if (key !== arg || !current.action) return {ok:false,notSubmitted:true,message:"提交前大厅状态已变化。"};
        const el = control(document, current.action);
        if (!el || forbidden(el)) return result(false,"匹配控件暂不可用。");
        if (["Xannaplay_pref_1631","wannaplay_pref_1631"].includes(el.id)) snapshot.realtimePrefs = true;
        const token = String(++snapshot.seq);
        snapshot.nodes.set(token,{e:el,title:label(el)});
        op = "click";
        arg = token;
    }
    if (op === "inspect") {
        if (arg === "结算" && url.pathname === "/table") {
            const panel = document.querySelector(".tablewithgame_seasaltpaper #game_result");
            if (!visible(panel)) return result(false, "官方结算尚未显示。");
            return {ok:true,桌号:url.searchParams.get("table"),官方显示:[...panel.querySelectorAll('[id^="score_entry_"]')].map(row=>({
                席位:row.id.slice("score_entry_".length),名次:norm(row.querySelector(".rank")?.innerText),
                玩家:norm(row.querySelector(".name")?.innerText),分数栏:norm(row.querySelector(".score")?.innerText)
            }))};
        }
        if (arg === "牌局" && (url.pathname === "/tableview" || /\/seasaltpaper\/?$/.test(url.pathname))) {
            // 诊断仅报告当前可见视角的公开席位、状态和操作区域，不枚举玩家手牌数据。
            const table = url.searchParams.get("table");
            function gameView(frame) {
                try {
                    const w = frame.contentWindow, address = new URL(w.location.href);
                    return address.origin === url.origin && address.searchParams.get("table") === table &&
                        /\/seasaltpaper\/?$/.test(address.pathname) ? w : null;
                } catch { return null; }
            }
            const frame = document.querySelector("#gameIframe");
            const host = url.pathname === "/tableview" ? (frame && gameView(frame)) : window;
            const g = host?.gameui, focus = g?.bgaInternal?.hotseat?.hotseat_focus;
            const nested = host?.document.getElementById("hotseat_iframe_" + focus);
            const current = nested ? gameView(nested) : host;
            const view = current?.gameui, d = current?.document;
            const realtime = realtimeAtTable(host, table);
            return {ok:true, 外壳:url.pathname, 桌号:table, 游戏框:!!frame, 内页就绪:!!g, 当前游戏:g?.game_name,
                回合制:realtime != null && (host === current || realtime === realtimeAtTable(current, table)) ? !realtime : null,
                热座模式:g?.hotseat_interface, 本机座位:g?.hotseatplayers, 当前视角:focus, 房主座位:g?.player_id,
                当前座位:view?.player_id, 手牌所属:d?.querySelector('[data-my-hand="true"]')?.dataset.playerId,
                玩家类型:Object.fromEntries(Object.entries(g?.players_metadata||{}).map(([id,v])=>[id,v.type])),
                公开名次板:Object.keys(g?.gamedatas?.players||{}), 回合: view?.gamedatas?.gamestate?.name,
                行动方:g?.bga?.players?.getActivePlayerIds?.(), 交接中:!!host?.document.getElementById("hotseat_mask")?.offsetWidth,
                操作区:norm(d?.querySelector("#maintitlebar_content")?.innerText).slice(0,170),
                页面提示:norm(d?.querySelector("#overall_messages_wrapper,#overall_messages")?.innerText).slice(0,120)};
        }
        const main = document.querySelector("#page-content") || document.body;
        const lines = (main.innerText || "").split("\n").map(norm).filter(Boolean);
        const messages = [...document.querySelectorAll('#overall_messages,#overall_messages_wrapper,#overall_messages_wrapper_source,.standard_popin,[role="alert"],[role="dialog"],#newArchiveCommentTextDisplay')]
            .filter(visible).map(el=>norm(el.innerText)).filter(Boolean);
        return { ok:true, 页面:document.title, 站点:url.hostname, 路径:url.pathname, 桌号:url.searchParams.get("table"),
            提示:messages.join(" / ").slice(0,400), 内容:lines.filter(x=>!query||x.toLocaleLowerCase().includes(query)).slice(0,80).map(x=>x.slice(0,170)) };
    }
    if (op === "list") {
        snapshot.nodes.clear();
        const entries = [];
        const selector = 'button,a,input,select,label,img[alt],div,span,[role="button"],[role="option"]';
        for (const e of document.querySelectorAll(selector)) {
            if (forbidden(e) || !visible(e)) continue;
            if (e.closest("#beginner_lobby_explanations_panels") && /^pageheader_/.test(e.id)) continue;
            const semantic = e.matches('button,a,input:not([type="hidden"]),select,label,[role="button"],[role="option"],[onclick]');
            if (!semantic && (getComputedStyle(e).cursor !== "pointer" || e.childElementCount > 4)) continue;
            const title = label(e);
            const match = [title,e.id,e.name,e.getAttribute("aria-label"),e.getAttribute("data-game")].join(" ").toLocaleLowerCase();
            if ((query && !match.includes(query)) || (!semantic && title.length > 140)) continue;
            const token = String(++snapshot.seq);
            snapshot.nodes.set(token, { e, title });
            entries.push({ 编号:token, 类型:e.tagName, 标识:e.id||undefined, 文字:title.slice(0,100), 禁用:e.disabled||e.matches(".disabled,[aria-disabled=true]")||undefined,
                选项:e.tagName === "SELECT" ? Array.from(e.options).map(o=>({值:o.value,名称:norm(o.textContent)})).slice(0,45) : undefined });
            if (entries.length === 100) break;
        }
        return {ok:true,控件:entries};
    }
    const [token, ...parts] = arg.split("|");
    const hit = snapshot.nodes.get(token);
    if (!hit || !visible(hit.e) || forbidden(hit.e) || label(hit.e) !== hit.title)
        return result(false, "控件已变化，请重新列出控件。");
    const e = hit.e;
    if (e.disabled || e.closest('.disabled,[aria-disabled="true"]')) return result(false, "控件不可用。");
    if (op === "click") {
        const a = e.closest("a");
        if (a && a.getAttribute("href") && !/^(?:#|javascript:void\(0\);?$)/i.test(a.getAttribute("href"))) {
            const target = new URL(a.href, location.href);
            if (target.origin !== url.origin || /account|premium|shop|payment/.test(target.pathname))
                return result(false, "此链接不属于当前站点的游戏操作。");
        }
        e.scrollIntoView({block:"center", inline:"nearest", behavior:"instant"});
        const r = e.getBoundingClientRect();
        let front = null;
        const reachable = [[.5,.5],[.25,.25],[.75,.25],[.25,.75],[.75,.75]].some(([x,y]) => {
            front = document.elementFromPoint(r.left+r.width*x,r.top+r.height*y);
            return front && (e === front || e.contains(front));
        });
        if (!reachable && realtimeClick)
            return {ok:false,notSubmitted:true,message:"目标控件暂被遮挡，等待页面更新。"};
        if (!reachable) return result(false, front ? "控件被其他面板遮挡：" + (front.id || front.tagName) + " " +
            label(front).slice(0,50) : "控件不在当前视口内。");
        snapshot.nodes.delete(token);
        const preference = /^(mm_pref_\d+_value_\d+)_priority_([19])$/.exec(e.id);
        if (preference && snapshot.realtimePrefs)
            snapshot.preferenceChange = {id:preference[1],priority:preference[2]};
        e.click();
        return result(true, "已向页面提交一次点击。");
    }
    const value = parts.join("|");
    if (op === "select" && e.tagName === "SELECT" && Array.from(e.options).some(o=>!o.disabled && o.value===value) ||
        op === "fill" && e.matches('input[type=text],input[type=search],input:not([type])') &&
            !e.readOnly && !/email|username|password/i.test(e.autocomplete || "") && value.length<=80) {
        e.value = value;
        e.dispatchEvent(new Event("input",{bubbles:true}));
        e.dispatchEvent(new Event("change",{bubbles:true}));
        return result(true,"已更新所选控件。");
    }
    return result(false,"该命令不适用于所选控件。");
}

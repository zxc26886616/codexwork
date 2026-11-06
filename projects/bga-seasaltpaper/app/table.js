function readSeaSaltDesk(armedTable, unconfirmedPhase, expectedOpponent, dispatch) {
    "use strict";
    let seatLabel = "";
    let clockSeconds = null, clockStatus = "unverified", clockSource = null, clockPhase = null, phaseSource = null, waitReason = null;
    const actionLabel = message => seatLabel ? seatLabel + " · " + message : message;
    const info = (kind, message, table) => ({ kind, message:kind === "action" ? actionLabel(message) : message, table, clockSeconds,
        clockStatus, clockSource, clockPhase, phaseSource, waitReason, clockReadAt: new Date().toISOString(),
        notSubmitted: !!dispatch?.commit && kind !== "action" });
    const outer = new URL(location.href);
    if (outer.protocol !== "https:" || !/(^|\.)boardgamearena\.com$/.test(outer.hostname))
        return info("idle", "当前页面不属于 BGA。");
    const site = /(^|\.)studio\.boardgamearena\.com$/.test(outer.hostname) ? "Studio" : "BGA";
    const number = outer.searchParams.get("table");
    let win = window;
    let doc = document;
    const tutorial = outer.pathname === "/tutorial" && outer.searchParams.get("game") === "seasaltpaper";
    let key = tutorial ? "教程/" + outer.searchParams.get("tutorial") : site + "/" + number;
    const byId = id => doc.getElementById(id);
    const all = selector => Array.from(doc.querySelectorAll(selector));
    const visible = el => el && el.getClientRects().length > 0 &&
        el.ownerDocument.defaultView.getComputedStyle(el).visibility !== "hidden" && !el.closest('[aria-hidden="true"]');
    const enabled = el => visible(el) && !el.matches(":disabled,.disabled,.bga-cards_disabled-card,[aria-disabled=true]");
    const label = el => (el?.textContent || "").replace(/\s+/g, " ").trim();
    if (outer.pathname === "/table" && /^\d+$/.test(number)) {
        if (!doc.querySelector(".tablewithgame_seasaltpaper")) return info("idle", "等候《海盐折纸》桌面信息。");
        const panel = byId("game_result");
        if (!visible(panel)) return info("wait", "尚无最终排名。进入游戏后可启动助手。");
        if (panel.querySelector(".score-entry") && !panel.querySelector(".score-entry.last-score-entry"))
            return info("wait", "正在读取结算名单。");
        const players = [];
        for (const row of panel.querySelectorAll('[id^="score_entry_"]')) {
            const rank = label(row.querySelector(".rank")).match(/\d+/);
            const scoreEl = row.querySelector(".score");
            // 加赛分和等级分在子节点中，统计只取主分数。
            const points = Array.from(scoreEl?.childNodes || []).filter(n => n.nodeType === 3)
                .map(n => n.textContent).join("").match(/-?\d+/);
            const id = row.id.slice("score_entry_".length);
            if (!/^[1-9]\d*$/.test(id)) return info("wait", "等待 BGA 显示完整的玩家座位。");
            // 热座的昵称是 strong，席位说明在它后面的独立文本节点。
            const name = label(row.querySelector(".playername") || row.querySelector(`.name #player_${id}_name`));
            if (!rank || !points || !name) return info("wait", "等待 BGA 显示完整名次和分数。");
            players.push({ id, name, rank: Number(rank[0]), score: Number(points[0]) });
        }
        const headers = all('.tablewithgame_seasaltpaper [id^="playerstatheader_"]');
        if (players.length < 2 || players.length > 4 ||
            headers.some(h => !players.some(p => h.id === "playerstatheader_" + p.id)))
            return info("wait", "正在读取结算名单。");
        return { kind: "result", message: "已读取官方结算", table: key, result: { site, table: number, players } };
    }
    const replayUrl = url => url.searchParams.has("replayFrom") || url.searchParams.get("replayLastTurn") === "1";
    const gameUrl = url => url.origin === outer.origin && /^\/(?:\d+\/)?seasaltpaper\/?$/.test(url.pathname) &&
        /^[1-9]\d*$/.test(number) && url.searchParams.get("table") === number && !replayUrl(url);
    function enterGameFrame(frame, seat) {
        try {
            if (!frame?.contentDocument) return false;
            const requested = new URL(frame.src);
            const actual = new URL(frame.contentWindow.location.href);
            if (!gameUrl(requested) || !gameUrl(actual) || (seat &&
                (requested.searchParams.get("testuser") !== seat || actual.searchParams.get("testuser") !== seat))) return false;
            win = frame.contentWindow;
            doc = frame.contentDocument;
            return true;
        } catch { return false; }
    }
    if (tutorial) {
        const frame = all("iframe").find(el => /\/tutorial\/tutorial\/frame\.html/.test(el.src));
        try { doc = frame?.contentDocument; win = frame?.contentWindow; }
        catch { return info("wait", "教程正在切换页面。"); }
        if (!doc || !win) return info("wait", "正在打开独立教学牌桌。");
    } else if (replayUrl(outer)) {
        return info("idle", "当前为回放。");
    } else if (outer.pathname === "/tableview" && /^[1-9]\d*$/.test(number)) {
        if (!enterGameFrame(doc.querySelector("iframe#gameIframe")))
            return info("wait", "等待《海盐折纸》牌桌载入。");
    } else if (!gameUrl(outer)) {
        return info("idle", "在窗口内打开海盐折纸牌桌、互动教程或已结束的桌号。");
    }
    const readOnly = () => win.gameui?.isSpectator || win.gameui?.bga?.players?.isCurrentPlayerSpectator?.() ||
        win.g_archive_mode || typeof win.g_replayFrom !== "undefined";
    if (expectedOpponent && armedTable?.startsWith(key + "/座位/") &&
        (win.navigator.onLine === false || visible(byId("connect_gs_status")))) {
        clockStatus = "offline";
        return info("offline", "网络已断开，等待恢复连接。", armedTable);
    }
    const hostWindow = win;
    const host = win.gameui;
    if (!tutorial && readOnly()) return info("idle", "当前为观战或回放。");
    if (!tutorial && host?.gamedatas?.gamestate?.name === "gameEnd")
        return info("ended", "牌局结束，准备读取官方排名。", key);
    let hotseat = false, focus = "", localTurn = false, mask, playGate;
    if (!tutorial && host?.hotseat_interface === "normal") {
        // 混合桌也有热座框架。只有全体参与者均被官方标为本机席位时才能跨座。
        const owner = String(host.bga?.players?.getCurrentPlayerId?.());
        const roster = Object.keys(host.gamedatas?.players || {});
        const localSeats = host.hotseatplayers;
        focus = String(host.bgaInternal?.hotseat?.hotseat_focus ?? "");
        mask = byId("hotseat_mask");
        playGate = mask?.querySelector("#hotseat_play_button");
        hotseat = host.game_name === "seasaltpaper" && String(host.table_id) === number &&
            owner === String(host.player_id) && roster.includes(owner) && roster.length >= 2 && roster.length <= 4 &&
            Array.isArray(localSeats) && roster.every(id => /^[1-9]\d*$/.test(id) && localSeats.includes(Number(id)) &&
                (id === owner || host.players_metadata?.[id]?.type === "hotseat"));
        if (hotseat) {
            key += "/热座/" + owner + "/" + roster.sort().join("-");
            if (!roster.includes(focus)) return info("wait", "等待本机热座交接。", key);
            seatLabel = focus === owner ? "房主" : "本地热座" + (roster.filter(id => id !== owner).indexOf(focus) + 1);
            localTurn = host.bga.players.getActivePlayerIds?.()?.some(id => String(id) === focus);
            if (focus !== owner && !enterGameFrame(byId("hotseat_iframe_" + focus), focus))
                return info("wait", "等待当前热座的牌桌载入。", key);
            if (readOnly()) return info("idle", "当前为观战或回放。");
        }
    }
    const game = win.gameui;
    const state = game?.gamedatas?.gamestate;
    if (!state || !byId("deck")) return info("wait", "等待牌桌载入。", hotseat ? key : undefined);
    const hand = doc.querySelector('[data-my-hand="true"]');
    if (!tutorial) {
        const seat = hand?.dataset.playerId || game.player_id;
        if (!hand || !/^[1-9]\d*$/.test(seat)) return info("wait", "等待自己的座位和手牌区域就绪。", hotseat ? key : undefined);
        if (hotseat) {
            if (hand.dataset.playerId !== focus || String(game.bga?.players?.getCurrentPlayerId?.()) !== focus ||
                String(game.player_id) !== focus || String(game.table_id) !== number || game.game_name !== "seasaltpaper")
                return info("wait", "等待热座身份与手牌同步。", key);
        } else {
            key += "/座位/" + seat;
            if (visible(mask) || (focus && focus !== String(seat)))
                return info("wait", "等待手动完成热座交接。", key);
        }
    }
    if (!tutorial && (game.gamedatas.extraSaltExpansion || game.gamedatas.extraPepperExpansion))
        return info("idle", "当前策略支持基础版，请在开桌时关闭扩展。");
    // 即时制批次只能操作本桌已核对的普通座位，回合内也重新核对公开名单。
    if (expectedOpponent && armedTable) {
        const seats = Object.keys(game.gamedatas?.players || {});
        if (hotseat || key !== armedTable || game.game_name !== "seasaltpaper" || String(game.table_id) !== number ||
            String(game.player_id) !== hand?.dataset.playerId || seats.length !== 2 ||
            !seats.includes(expectedOpponent) || !seats.includes(String(game.player_id)) ||
            expectedOpponent === String(game.player_id) || game.players_metadata?.[expectedOpponent]?.type === "hotseat")
            return info("stalled", "即时制座位或对手发生变化，已停止操作。", key);
    }
    if (tutorial && visible(doc.querySelector("#newArchiveCommentTextDisplay #end_tutorial_play_now")))
        return info("tutorialDone", "已完成单人教程。教学回放不计入正式战绩。", key);
    const track = win.__seaSaltDeskTrack ??= { last: "", since: 0 };
    if (!armedTable || armedTable !== key) {
        track.last = "";
        track.lockedSince = track.submittedPhase = null;
        return info("ready", tutorial ? "教学桌已就绪，可启动单人验证。" :
            hotseat ? "本机热座牌桌已就绪，可启动轮流出牌。" : "牌桌已就绪，可启动自动出牌。", key);
    }
    if (win.navigator.onLine === false) return info("wait", "网络已断开，等待恢复连接。", key);
    if (hotseat) {
        if (!visible(mask)) hostWindow.__seaSaltDeskGate = "";
        if (!localTurn || !game.bga.players.isCurrentPlayerActive?.()) {
            track.lockedSince = null;
            return info("wait", "等待本机热座的行动回合。", key);
        }
        if (visible(mask)) {
            const handoff = key + "/" + focus;
            if (!enabled(playGate) || hostWindow.__seaSaltDeskGate === handoff)
                return info("wait", "等待热座交接确认。", key);
            hostWindow.__seaSaltDeskGate = handoff;
            playGate.click();
            return info("action", "确认本机热座交接。", key);
        }
    }
    const comment = label(byId("newArchiveCommentTextDisplay"));
    const cards = (area, selector = ".card") => Array.from(area?.querySelectorAll(selector) || []).filter(visible);
    const phase = String(game.player_id || hand?.dataset.playerId || "") + "/" + state.name;
    const moveNumber = () => {
        const text = label(byId("move_nbr"));
        return /^\d+$/.test(text) && Number.isSafeInteger(Number(text)) ? Number(text) : null;
    };
    function click(el, reason, isPairSelection = false, endTurn = false, roundAck = false) {
        if (expectedOpponent && !enabled(el)) {
            const alternative = legalFallback();
            if (alternative) { el = alternative; isPairSelection = false; endTurn = false; reason = "保底行动：" + (label(el).slice(0,45) || cardName(el)); }
        }
        if (!enabled(el)) {
            const missing = "missing:" + state.name + ":" + reason;
            if (track.last !== missing) { track.last = missing; track.since = Date.now(); }
            if (Date.now() - track.since > 15000)
                return info("stalled", "找不到可用的操作控件，请检查牌桌：" + reason, key);
            return info("wait", "等待可操作控件：" + reason, key);
        }
        const signature = [state.name, state.active_player, comment, el.id, label(el).slice(0, 80),
            ...cards(hand).map(c => c.id + ":" + c.matches(".selected,.bga-cards_selected-card")),
            label(byId("deck")), label(byId("discard1")), label(byId("discard2")),
            label(byId("cards-points-counter")), ...cards(byId("pick")).map(c => c.id)].join("|");
        if (expectedOpponent && dispatch) {
            // 先将意图交给宿主。仅同一文档、局面和目标可以消费这次派发。
            const server = game.last_server_state || state;
            const move = moveNumber();
            const documentId = track.documentId ??= crypto.randomUUID();
            const serverPhase = [server.id ?? "", server.name, server.active_player ?? ""].join("/");
            const board = JSON.stringify([hand, byId("pick"), byId("discard-pick"), byId("discard1"), byId("discard2"),
                hand?.closest(".player-table")?.querySelector(".table.cards")].map(area => cards(area).map(c => c.id).filter(Boolean).sort()));
            const local = isPairSelection && state.name === "playCards" && hand?.contains(el) &&
                el.matches(".card.bga-cards_selectable-card,.card.selectable") ? el.id : "";
            const actionKind = roundAck ? "round-ack" : state.name === "playCards" && (endTurn || el.id === "endTurn_button") ? "end-turn" :
                state.name === "takeCards" && byId("deck")?.contains(el) ? "draw-deck" :
                state.name === "putDiscardPile" && (el === byId("discard1") || el === byId("discard2")) ? "place-discard" :
                dispatch.recoverDraw && state.name === "takeCards" && el.closest("#discard1,#discard2") ? "recover-discard" :
                dispatch.recoverDraw && state.name === "chooseCard" && byId("pick")?.contains(el) ? "keep-drawn" : null;
            const officialOwn = roundAck || !!game.last_server_state && !game.on_client_state && active === true &&
                server.type === "activeplayer" && state.type === server.type && state.name === server.name &&
                state.id === server.id && String(server.active_player) === String(game.player_id) &&
                String(state.active_player) === String(game.player_id);
            const plan = {documentId, phase:serverPhase, move, board, target:signature, local, actionKind, officialOwn,
                recoveryCard:actionKind === "recover-discard" ? el.id : null};
            if (!dispatch.commit) return { ...info("plan", reason, key), plan };
            if (game.on_client_state || !el.isConnected || Object.keys(plan).some(k => plan[k] !== dispatch.commit[k]) ||
                Date.now() > dispatch.expires)
                return info("not-sent", "局面或文档已变化，重新选择合法行动。", key);
            track.last = signature;
            track.since = Date.now();
            track.submittedPhase = phase;
            el.click();
            return { ...info("action", reason, key), phase };
        }
        if (track.last === signature) {
            if (Date.now() - track.since > 15000) return info("stalled", "页面未确认上一步。请检查牌桌后重新启动。", key);
            return info("wait", "等待页面确认：" + reason, key);
        }
        track.last = signature;
        track.since = Date.now();
        if (!tutorial) track.submittedPhase = phase;
        el.click();
        return { ...info("action", reason, key), phase: tutorial ? null : phase };
    }
    if (tutorial) {
        if (visible(byId("tutorial_end")) || visible(byId("tutorial_conclusion_title")) ||
            /(?:completed (?:this|the) tutorial|finished (?:this|the) tutorial|完成了?(?:本|此|这个)教程)/i.test(comment))
            return info("tutorialDone", "已到达教程结尾。教学演示单独处理。", key);
        const next = byId("newArchiveCommentNext");
        if (enabled(next)) return click(next, "进入下一步教程");
        // The public lesson has a prescribed move. Follow its annotated card or turn button.
        // In between annotations BGA plays the recorded moves by itself.
        const popup = byId("newArchiveComment");
        const id = popup?.querySelector('[id^="newArchiveCommentSaveModify_"]')?.id.split("_").at(-1);
        const annotation = id && byId("archiveComment_" + id);
        if (!visible(popup) || label(annotation?.querySelector(".archiveComment_continuemode")) !== "1")
            return info("wait", "等待教程演示下一步。", key);
        const target = byId(label(annotation.querySelector(".archiveComment_anchor")));
        if (target?.matches("#endTurn_button, #pick > .card, #discard1 > .card, #discard2 > .card") &&
            state.name !== "putDiscardPile") return click(target, "执行教程指定的操作");
    }
    if (expectedOpponent) {
        const server = game.last_server_state || state;
        phaseSource = game.last_server_state ? "last-server-state" : "gamedatas-state";
        clockPhase = [server.id ?? "", server.name, server.active_player ?? ""].join("/");
        // 客户端临时阶段的 DOM 棋钟不是可信余量；仍将等待交给宿主做有界只读核对。
        if (game.on_client_state) {
            waitReason = "client-state";
            const current = win.bgaGameData?.isRealTime, legacy = game.bRealtime;
            clockStatus = current === false || legacy === false ? "not-realtime" :
                game.last_server_state && server.active_player && String(server.active_player) !== String(game.player_id)
                    ? "not-own-turn" : "client-state";
            return info("wait", "等待游戏服务器连接及正式回合恢复。", key);
        }
    }
    if (state.type === "game" || state.type === "manager") {
        clockStatus = "server-transition";
        track.last = "";
        track.lockedSince = null;
        return info("wait", "等待服务器切换回合或结算。", key);
    }
    const active = typeof game.isCurrentPlayerActive === "function" ? game.isCurrentPlayerActive()
        : game.bga?.players?.isCurrentPlayerActive?.();
    if (!active) {
        clockStatus = "not-own-turn";
        track.last = ""; track.lockedSince = null;
        let opponentTurn;
        if (expectedOpponent && active === false) {
            const server = game.last_server_state, current = win.bgaGameData?.isRealTime, legacy = game.bRealtime;
            if (dispatch && !dispatch.commit && track.documentId && (current === true || legacy === true) &&
                current !== false && legacy !== false && server?.type === "activeplayer" && state.type === server.type &&
                /^[1-9]\d*$/.test(String(server.id)) && /^[a-zA-Z][a-zA-Z0-9_]*$/.test(server.name ?? "") &&
                state.name === server.name && state.id === server.id && String(server.active_player) === expectedOpponent &&
                String(state.active_player) === expectedOpponent) {
                const move = moveNumber();
                if (move !== null) opponentTurn = {documentId:track.documentId, phase:clockPhase, move};
            }
            track.passiveSince ??= Date.now();
            if (Date.now() - track.passiveSince > 15000)
                return {...info("refresh-passive", "等待对手超过 15 秒，重新读取原桌的行动方。", key), opponentTurn};
        }
        return {...info("wait", "等待其他座位行动。", key), opponentTurn};
    }
    track.passiveSince = null;
    const matchedScore = expectedOpponent && state.name === "beforeEndRound";
    if (matchedScore) {
        const server = game.last_server_state, self = String(game.player_id);
        // 多人确认时 active_player 可能保留上一手。显式待确认名单优先，不能据残留按钮出手。
        const includesSelf = snapshot => {
            if (snapshot.type === "multipleactiveplayer" && snapshot.multiactive != null)
                return Array.isArray(snapshot.multiactive) && snapshot.multiactive.some(id => String(id) === self);
            return String(snapshot.active_player) === self;
        };
        if (active !== true || !server || String(server.id) !== "79" || state.id !== server.id ||
            server.name !== state.name || state.type !== server.type ||
            !["activeplayer","multipleactiveplayer"].includes(server.type) || !includesSelf(server) || !includesSelf(state))
            return info("wait", "等待原桌确认己方仍须确认本轮得分。", key);
    }
    if (expectedOpponent && active === true) {
        // 与大厅 realtimeAtTable 使用相同的布尔值约定，在对手回合、断线或客户端临时阶段不发布己方棋钟。
        const current = win.bgaGameData?.isRealTime, legacy = game.bRealtime;
        if (current === false || legacy === false || (current !== true && legacy !== true)) {
            clockStatus = "not-realtime";
            return info("wait", "等待牌桌重新确认即时制。", key);
        }
        clockStatus = "unknown";
        const server = game.last_server_state || state;
        // 行动方也须属于绑定己方，不能借用上一阶段或对手的显示。
        if (matchedScore || String(server.active_player) === String(game.player_id)) {
            clockSource = "timeToThink_" + game.player_id;
            const clock = byId(clockSource);
            const shown = visible(clock) && /^(-?)(\d{1,2}):([0-5]\d)$/.exec(label(clock));
            if (shown) {
                clockSeconds = (shown[1] ? -1 : 1) * (Number(shown[2]) * 60 + Number(shown[3]));
                clockStatus = "known";
            }
        }
    }
    if (game.isInterfaceLocked?.()) {
        waitReason = "interface-lock";
        track.lockedSince ??= Date.now();
        if (!tutorial && Date.now() - track.lockedSince > 30000) {
            if (track.submittedPhase && !unconfirmedPhase)
                return { ...info("resync", "操作回执超时，正在向原桌重新读取局面。", key), phase: track.submittedPhase };
            if (expectedOpponent && dispatch)
                return info("resync", "接口仍在等待，重新读取原桌的官方局面。", key);
            return info("stalled", "牌桌未恢复，请查看网络状态后重新启动。", key);
        }
        return info("wait", "等待牌桌动画或网络确认。", key);
    }
    track.lockedSince = null;
    // 若服务器仍显示相同座位和操作阶段，无法确认上一次操作是否已提交。
    if (unconfirmedPhase === phase)
        return info("stalled", "重连后仍处于上次操作阶段，已暂停以避免重复出牌。", key);
    const playable = el => enabled(el) && !el.classList.contains("disabled-card");
    const face = el => el?.dataset.side === "back" ? undefined :
        (el?.dataset.category ? el.dataset : el?.querySelector(".front[data-category]")?.dataset);
    const families = [[], [], ["螃蟹","小船","鱼","游泳者","鲨鱼"], ["贝壳","章鱼","企鹅","水手"],
        ["灯塔","鱼群","企鹅群","船长"]];
    const colors = ["白色","深蓝色","浅蓝色","黑色","黄色","绿色","紫色","灰色","浅橙色","粉色","橙色"];
    function cardName(el) {
        const f = face(el);
        if (!f) return "卡牌";
        const family = +f.category === 1 ? "美人鱼" : families[+f.category]?.[Number(f.family) - 1];
        return (colors[+f.color] || "") + (family || "卡牌");
    }
    const held = cards(hand).map(face).filter(Boolean);
    const tableArea = hand?.closest(".player-table")?.querySelector(".table.cards");
    const owned = held.concat(cards(tableArea).map(face).filter(Boolean));
    function value(el) {
        const f = face(el);
        if (!f) return 0;
        const count = (list, c, t) => list.filter(x => +x.category === c && +x.family === t).length;
        const type = +f.family;
        const sameColor = owned.filter(x => x.color === f.color).length;
        let points = 1;
        switch (+f.category) {
            case 1: points = 6 + 2 * owned.filter(x => +x.category === 1).length; break;
            case 2: {
                const partner = type === 4 ? 5 : type === 5 ? 4 : type;
                points = count(held, 2, partner) ? 5 : 2;
                break;
            }
            case 3: points = count(owned, 3, type) ? [0, 4, 5, 4, 7][type] : type === 3 ? 3 : 1; break;
            case 4: {
                const target = [[0, 0], [2, 2], [2, 3], [3, 3], [3, 4]][type];
                points = target ? 1 + count(owned, ...target) * [0, 1, 1, 2, 3][type] : 1;
                break;
            }
        }
        return points + Math.min(sameColor, 5) * 0.15;
    }
    const best = candidates => candidates.filter(playable).sort((a, b) => value(b) - value(a))[0];
    const piles = [byId("discard1"), byId("discard2")].filter(visible);
    const topCard = pile => cards(pile).at(-1);
    if (dispatch?.recoverDraw && state.name === "takeCards") {
        const server = game.last_server_state;
        const allows = (args, pile) => Array.isArray(args?.canTakeFromDiscard) &&
            args.canTakeFromDiscard.some(id => String(id) === pile.id.slice(-1));
        const card = piles.filter(p => allows(server?.args, p) && allows(state.args, p)).map(topCard)
            .find(el => el?.id && playable(el) && !el.closest('.disabled,[aria-disabled="true"]'));
        if (card) return click(card, "抽牌回执待定，从允许的弃牌堆取明牌");
        waitReason = "draw-no-safe-discard";
        return info("wait", "等待原桌给出合法明牌或进入选牌阶段。", key);
    }
    const choices = sel => all(sel).filter(playable);
    const actions = choices("#generalactions :is(a,button), #additional_actions :is(a,button), #maintitlebar_content button, #page-title button");
    const named = (rx, translation) => actions.find(b => rx.test(label(b)) ||
        (translation && typeof win._ === "function" && label(b) === win._(translation)));
    if (matchedScore) {
        const seen = actions.find(el => el.closest("#generalactions,#additional_actions") &&
            !el.closest('.disabled,[aria-disabled="true"]') && (el.id === "seen_button" ||
            ["Seen","已阅","已读","已讀","我确认完了"].includes(label(el)) ||
            (typeof win._ === "function" && label(el) === win._("Seen"))));
        return seen ? click(seen, "确认本轮得分", false, false, true) :
            info("wait", "等待本轮得分确认按钮。", key);
    }
    function legalFallback() {
        // 仅在已核对的己方行动回合使用。优先结束或确认，其次选服务器列出的行动或标记可选的牌。
        const possible = new Set([...(Array.isArray(state.possibleactions) ? state.possibleactions : []),
            ...(Array.isArray(state.args?.possibleactions) ? state.args.possibleactions : [])].map(x => String(x).toLowerCase()));
        const buttons = actions.filter(b => b.closest("#generalactions,#additional_actions") &&
            !b.closest('.disabled,[aria-disabled="true"]') && !/undo|cancel|quit|resign|abandon|leave|giveup|concede|kick|propos|switch|clock|think/i.test(b.id) &&
            !/撤销|取消|退出|认输|弃权|踢出|切换|投票/i.test(label(b)));
        return ["seen_button","endTurn_button","endRound_button","immediateEndRound_button","playCards_button"]
            .map(id => buttons.find(b => b.id === id)).find(Boolean) ||
            buttons.find(b => possible.has(b.id.replace(/[-_]button$/i,"").toLowerCase()) ||
                /^choosePlayer[1-9]\d*-button$/.test(b.id)) ||
            [byId("pick"),byId("discard-pick"),hand,...piles,byId("deck")].flatMap(area => cards(area))
                .find(card => playable(card) && card.matches(".bga-cards_selectable-card,.selectable") &&
                    !card.matches(".bga-cards_selected-card,.selected"));
    }
    // 官方 10 秒告警出现后优先结束当前行动。只在对应阶段使用已知动作，按钮可能只有本地化标签。
    if (clockSeconds !== null && clockSeconds <= 10) {
        const urgent = (el, reason) => click(el, "己方棋钟 " + clockSeconds + " 秒，" + reason);
        const button = (id, translation, aliases) => actions.find(el => el.closest("#generalactions,#additional_actions") &&
            !el.closest('.disabled,[aria-disabled="true"]') && (el.id === id || aliases.includes(label(el)) ||
            (typeof win._ === "function" && label(el) === win._(translation))));
        if (state.name === "playCards") {
            const end = button("endTurn_button", "End turn", ["End turn","结束回合","结束行动","結束回合"]);
            if (end) return click(end, "己方棋钟 " + clockSeconds + " 秒，结束本回合", false, true);
        }
        if (state.name === "beforeEndRound") {
            const seen = button("seen_button", "Seen", ["Seen","已阅","已读","已讀"]);
            if (seen) return urgent(seen, "确认本轮得分");
            return info("wait", "己方棋钟紧张，等待可识别的本轮得分确认控件。", key);
        }
        if (state.name === "takeCards" && Array.isArray(state.args?.canTakeFromDiscard)) {
            // 取明牌直接进入出牌阶段，省去牌库抽二选一和弃牌两个服务器往返。
            const top = piles.filter(p => state.args.canTakeFromDiscard.some(id => String(id) === p.id.slice(-1)))
                .map(topCard).find(el => el && playable(el) && !el.closest('.disabled,[aria-disabled="true"]'));
            if (top) return urgent(top, "从允许的弃牌堆取明牌");
        }
    }
    switch (state.name) {
        case "takeCards": {
            const available = piles.filter(p => state.args?.canTakeFromDiscard?.map(Number).includes(+p.id.slice(-1)) ?? !!topCard(p));
            const pile = available.sort((a, b) => value(topCard(b)) - value(topCard(a)))[0];
            const deck = cards(byId("deck")).at(-1) || byId("deck");
            if (pile && (value(topCard(pile)) >= 4 || state.args?.canTakeFromDeck === false))
                return click(topCard(pile), "从弃牌堆" + pile.id.slice(-1) + "拿取" + cardName(topCard(pile)));
            return click(state.args?.canTakeFromDeck === false ? null : deck, "从牌库抽牌");
        }
        case "chooseCard": {
            const choice = best(cards(byId("pick")));
            return click(choice, "选择保留" + cardName(choice));
        }
        case "putDiscardPile": {
            const pile = piles.find(p => !topCard(p)) || piles[0];
            return click(pile, "把剩余的牌放入弃牌堆" + (pile?.id.slice(-1) || ""));
        }
        case "chooseDiscardPile": return click(piles.filter(p => topCard(p)).sort((a, b) => value(topCard(b)) - value(topCard(a)))[0], "发动螃蟹效果");
        case "chooseDiscardCard": return click(best(cards(byId("discard-pick"))), "从弃牌堆取牌");
        case "chooseOpponent": return click(choices('[data-can-steal="true"] .card')[0] ||
            choices('[id^="choosePlayer"][id$="-button"]')[0], "选择可抽牌的对手");
        case "beforeEndRound": return click(byId("seen_button") || named(/seen|看过|已阅|确认|知道|继续|已读|查看完毕/i, "Seen") ||
            (actions.length === 1 ? actions[0] : null), "确认本轮得分");
        case "playCards": {
            const mermaids = named(/mermaid|美人鱼|人魚|人鱼/i);
            if (state.args?.hasFourMermaids && mermaids) return click(mermaids, "亮出美人鱼");
            const play = byId("playCards_button");
            if (enabled(play)) return click(play, "打出选中的组合");
            const pairCard = cards(hand).find(el => playable(el) && (el.matches(".bga-cards_selectable-card,.selectable") ||
                (tutorial && el.dataset.category)) &&
                !el.matches(".bga-cards_selected-card,.selected") && +face(el)?.category === 2);
            if (pairCard) return click(pairCard, "选中" + cardName(pairCard) + "用于配对", true);
            if (enabled(byId("endRound_button"))) return click(byId("endRound_button"), "宣布最后机会");
            if (enabled(byId("immediateEndRound_button"))) return click(byId("immediateEndRound_button"), "喊停并结算本轮");
            return click(byId("endTurn_button") || named(/end turn|结束回合|结束行动|結束回合/i, "End turn"), "结束本回合", false, true);
        }
        default:
            if (expectedOpponent) return click(null, "等待平台标记合法行动（" + state.name + "）");
            return info("unsupported", "当前操作尚未支持，请在牌桌上手动完成这一步。", key);
    }
}

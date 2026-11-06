function switchSeaSaltTable(expected, permit, issued) {
    "use strict";
    const outer = new URL(location.href);
    const number = /^[1-9]\d{0,17}$/;
    const replay = url => url.searchParams.has("replayFrom") || url.searchParams.get("replayLastTurn") === "1";
    if (outer.origin !== expected.origin || outer.protocol !== "https:" || outer.port ||
        !/(^|\.)boardgamearena\.com$/.test(outer.hostname) || /(^|\.)studio\.boardgamearena\.com$/.test(outer.hostname) ||
        !number.test(expected.table) || !number.test(expected.host) ||
        outer.searchParams.get("table") !== expected.table || replay(outer)) return "blocked";
    const gameUrl = url => url.origin === outer.origin && /^\/(?:\d+\/)?seasaltpaper\/?$/.test(url.pathname) &&
        url.searchParams.get("table") === expected.table && !replay(url);
    function frameView(frame, guestId) {
        if (!frame?.contentDocument) return null;
        const src = new URL(frame.src), actual = new URL(frame.contentWindow.location.href);
        if (!gameUrl(src) || !gameUrl(actual) || (guestId &&
            [src, actual].some(url => url.searchParams.get("testuser") !== guestId))) return null;
        return frame.contentWindow;
    }
    let main = window;
    if (outer.pathname === "/tableview") {
        if (!window.globalUserInfos) return "wait";
        if (String(window.globalUserInfos.id) !== expected.host) return "blocked";
        main = frameView(document.querySelector("iframe#gameIframe"));
        if (!main) return "wait";
    } else if (!gameUrl(outer)) return "blocked";

    const owner = main.gameui;
    if (!owner?.gamedatas?.players) return "wait";
    const roster = Object.keys(owner.gamedatas.players);
    const guestId = roster.find(id => id !== expected.host);
    if (roster.length !== 2 || !roster.includes(expected.host) || !number.test(guestId) ||
        owner.hotseat_interface !== "normal" || owner.players_metadata?.[guestId]?.type !== "hotseat" ||
        roster.some(id => !owner.hotseatplayers?.includes(Number(id))) ||
        owner.gamedatas.players[guestId].name !== expected.guest) return "blocked";
    const guest = frameView(main.document.getElementById("hotseat_iframe_" + guestId), guestId);
    if (!guest?.gameui?.gamedatas?.gamestate) return "wait";
    const seats = [[expected.host, main], [guestId, guest]];
    const visible = e => !!e?.isConnected && e.getClientRects().length > 0 &&
        !e.closest('[inert],[aria-hidden="true"]') && e.checkVisibility({opacityProperty:true, visibilityProperty:true});
    const normal = text => (text || "").replace(/\s+/g, " ").trim();
    function button(key, el) {
        if (!visible(el) || el.matches(":disabled,.disabled,[aria-disabled=true]")) return "wait";
        if (key === permit) el.click();
        return key;
    }
    for (const [id, view] of seats) {
        const g = view.gameui;
        const players = g.gamedatas.players;
        if (!players || Object.keys(players).length !== 2 || !players[expected.host] || players[guestId]?.name !== expected.guest ||
            g.game_name !== "seasaltpaper" || String(g.table_id) !== expected.table || String(g.player_id) !== id ||
            String(g.bga?.players?.getCurrentPlayerId?.()) !== id || g.bga?.players?.isCurrentPlayerSpectator?.() !== false ||
            g.isSpectator || view.g_archive_mode || typeof view.g_replayFrom !== "undefined" ||
            g.gamedatas.gamestate?.name === "gameEnd" ||
            (id === guestId && g.hotseat_interface !== "hotseataccount")) return "blocked";
    }
    const modes = seats.map(([, v]) => v.gameui.bga?.tableOptions?.isTurnBased?.());
    const converted = modes.every(mode => mode === true);
    if (!converted && !modes.every(mode => mode === false)) return "wait";

    const focus = String(owner.bgaInternal?.hotseat?.hotseat_focus);
    const current = seats.find(([id]) => id === focus);
    if (!current) return "wait";
    const outstanding = [];
    let voting = false;
    for (const [id, view] of seats) {
        const doc = view.document, decision = view.gameui.gamedatas.decision;
        if (!decision) return "wait";
        if (decision.decision_type !== "none" && decision.decision_type !== "switch_tb") return "blocked";
        if (decision.decision_type === "switch_tb" && String(decision.decision_refused) === "true") return "blocked";
        const panel = doc.getElementById("table-decision");
        if (!visible(panel)) {
            if (decision.decision_type === "switch_tb") voting = true;
            continue;
        }
        // 实时通知只刷新投票面板，gamedatas.decision 可能仍是入桌时的 none。
        const prompt = "Would you like to transform this table in a Turn-based table ?";
        const translated = normal(typeof view.__ === "function" ? view.__("lang_mainsite", prompt) : prompt);
        const title = normal(panel.querySelector("#decision-title")?.textContent);
        if (!translated || (title !== translated && !title.startsWith(translated + " ("))) return "blocked";
        voting = true;
        const waiting = [...panel.querySelectorAll("#decision-players-undecided b")];
        if (view.gameui.gamedatas.players[expected.host].name === expected.guest) return "blocked";
        const key = "agree/" + id;
        if (!issued.includes("propose/" + id) && (!issued.includes(key) || permit === key) &&
            waiting.some(el => normal(el.textContent) === view.gameui.gamedatas.players[id].name))
            outstanding.push([key, panel.querySelector("#decision_yes")]);
    }
    if (converted) return "done";
    if (voting && !outstanding.length) return "wait";
    const mask = main.document.getElementById("hotseat_mask");
    if (visible(mask)) return button("gate/" + focus, mask.querySelector("#hotseat_play_button"));
    if (voting) return button(outstanding[0][0], outstanding[0][1]);
    const doc = current[1].document;
    const propose = doc.getElementById("ingame_menu_switch_tb");
    if (!propose) return "wait";
    if (!visible(propose)) return button("menu/" + focus, doc.getElementById("ingame_menu"));
    const key = "propose/" + focus;
    if (issued.some(action => action.startsWith("propose/")) && permit !== key) return "wait";
    return button(key, propose);
}

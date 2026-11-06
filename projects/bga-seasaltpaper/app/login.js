function loginPage(origin, operation, username, value) {
    "use strict";
    if (window.top !== window || location.origin !== origin) return "wrong-origin";
    const player = window.globalUserInfos;
    if (player && Number(player.id) > 0) {
        return String(player.name).toLowerCase() === username.toLowerCase() ? "signed-in" : "other-account";
    }
    const visible = (el, allowDisabledButton = false) => {
        if (!el || !el.getClientRects().length) return false;
        for (let p = el; p; p = p.parentElement) {
            const s = getComputedStyle(p);
            if (s.opacity === "0" || s.visibility === "hidden" ||
                (s.pointerEvents === "none" && !(allowDisabledButton && p === el)) ||
                p.inert || p.getAttribute("aria-hidden") === "true") return false;
        }
        return true;
    };
    const one = (scope, selector) => [...scope.querySelectorAll(selector)].find(el => visible(el));
    // Cookie and verification notices must be handled before interacting with the form behind them.
    if (visible(document.getElementById("didomi-notice"))) return "cookies";
    if (one(document, 'input[autocomplete="one-time-code"],iframe[src*="bframe"],iframe[src*="frame=challenge"]'))
        return "challenge";
    if (one(document, 'iframe[src*="/recaptcha/"][src*="/anchor"]:not([src*="size=invisible"]),iframe[src*="frame=checkbox"],iframe[src*="challenges.cloudflare.com"]') &&
        ![...document.querySelectorAll('[name="g-recaptcha-response"],[name="h-captcha-response"],[name="cf-turnstile-response"]')].some(el => el.value))
        return "challenge";
    if (location.pathname !== "/account" || new URLSearchParams(location.search).has("page")) return "not-login";
    const root = document.querySelector("#account-module");
    if (!root) return "loading";
    for (const [stage, selector] of [["user", ".bga-account-manager-form__step1"], ["pass", ".bga-account-manager-form__step2"]]) {
        const area = one(root, selector);
        const form = area && one(area, "form");
        if (!form || !visible(form)) continue;
        if (new URL(form.action || location.href, location.href).origin !== origin) return "wrong-origin";
        const errors = [...area.querySelectorAll('[role="alert"],form .text-bga-input-red,form .text-red-500')]
            .filter(el => visible(el) && el.textContent.trim());
        if (stage === "user" && errors.some(el => /^username not registered[.!]?$/i.test(el.textContent.trim())))
            return "unknown-user";
        if (errors.length || one(area, '.bga-emailfield__error-mark,[aria-invalid="true"]')) return "rejected";
        const field = one(form, stage === "user" ? 'input[name="email"]' : 'input[type="password"]:not([autocomplete="new-password"])');
        const submit = [...form.querySelectorAll('a.bga-button--blue[href="#"]')].find(el => visible(el, true));
        if (!field || !submit || field.disabled || field.readOnly) return "loading";
        if (stage === "pass") {
            const echoed = form.querySelector('input[autocomplete="username"][readonly]');
            if (!echoed?.value.trim()) return "loading";
            if (echoed.value.trim().toLowerCase() !== username.toLowerCase()) return "other-account";
        }
        if (operation !== stage) return stage;
        if (field.value !== value) {
            field.value = value;
            field.dispatchEvent(new Event("input", {bubbles:true}));
            field.dispatchEvent(new Event("change", {bubbles:true}));
            // BGA 的表单组件异步同步输入值，在下一次轮询点击更新后的按钮。
            return "loading";
        }
        if (!visible(submit) || submit.matches('[aria-disabled="true"],.disabled')) return "loading";
        submit.click();
        return "sent-" + stage;
    }
    return "loading";
}

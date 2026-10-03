(() => {
    const t = key => window.presenterText?.(key) || key;
    const elements = {
        presenter: document.getElementById("dashboard-presenter-state"),
        browser: document.getElementById("dashboard-browser-state"),
        title: document.getElementById("dashboard-current-title"),
        position: document.getElementById("dashboard-position"),
        ffprobe: document.getElementById("dashboard-ffprobe"),
        scanner: document.getElementById("dashboard-scanner")
    };
    if (Object.values(elements).some(element => !element)) return;

    const formatClock = value => {
        if (!value) return "--:--:--";
        const text = String(value);
        const match = /\d{2}:\d{2}:\d{2}/.exec(text);
        return match ? match[0] : text;
    };

    const refresh = async () => {
        try {
            const response = await fetch("/api/status", {
                credentials: "same-origin",
                headers: { "Accept": "application/json" },
                cache: "no-store"
            });
            if (!response.ok) return;
            const status = await response.json();
            if (status.update) {
                const update = status.update;
                document.getElementById("update-versions").textContent = t("Installiert: {0} · Verfügbar: {1}").replace("{0}", update.installedVersion).replace("{1}", update.availableVersion || "–");
                document.getElementById("update-status").textContent = t(update.phase) + (update.phase === "Downloading" ? ` ${update.progress}%` : "");
                document.getElementById("update-countdown").textContent = update.installAtUtc ? t("Installation in {0} Sekunden").replace("{0}", Math.max(0, Math.ceil((Date.parse(update.installAtUtc) - Date.now()) / 1000))) : "";
                document.getElementById("update-error").textContent = update.error ? t(update.error) : "";
                const automatic = document.getElementById("update-automatic");
                if (document.activeElement !== automatic && !automatic.dataset.edited) automatic.checked = update.automaticUpdatesEnabled;
                automatic.disabled = update.phase === "Installing";
                document.getElementById("update-install").disabled = update.phase === "Installing";
                document.getElementById("update-postpone").disabled = update.phase !== "Ready" || !update.automaticUpdatesEnabled;
            }
            elements.presenter.textContent = t(status.presenterState);
            elements.browser.textContent = t("Browser: {0}").replace("{0}", t(status.browserConnected ? "Verbunden" : "Getrennt"));
            elements.title.textContent = status.currentTitle || "–";
            elements.position.textContent = `${formatClock(status.position)} / ${formatClock(status.duration)}`;
            elements.ffprobe.textContent = t(status.ffprobeAvailable ? "OK" : "Nicht verfügbar");
            if (status.mediaScannerRunning) elements.scanner.textContent = t("Läuft");
            else if (status.mediaScannerError) elements.scanner.textContent = t("Fehler");
            else elements.scanner.textContent = t("Bereit");
        } catch {
            elements.browser.textContent = t("Browser: Status nicht erreichbar");
        }
    };

    document.getElementById("update-automatic")?.addEventListener("change", event => { event.target.dataset.edited = "true"; });
    refresh();
    window.setInterval(refresh, 2000);
})();

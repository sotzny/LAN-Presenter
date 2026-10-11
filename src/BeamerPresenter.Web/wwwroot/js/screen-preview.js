(function initializeScreenPreview() {
    window.presenterScreenPreviewCleanup?.();
    const image = document.getElementById("screen-preview-image");
    const mode = document.getElementById("screen-preview-mode");
    const status = document.getElementById("screen-preview-status");
    const timestamp = document.getElementById("screen-preview-time");
    if (!image || !mode || !status || !timestamp) return;
    const t = key => window.presenterText?.(key) || key;
    let timer, controller, currentUrl;
    let busy = false, stopped = false, revision = 0, refreshAgain = false;

    const clearImage = () => {
        image.hidden = true;
        image.removeAttribute("src");
        timestamp.hidden = true;
        if (currentUrl) URL.revokeObjectURL(currentUrl);
        currentUrl = undefined;
    };
    const cleanup = () => {
        stopped = true;
        revision++;
        clearTimeout(timer);
        controller?.abort();
        clearImage();
        mode.removeEventListener("change", restart);
        document.removeEventListener("visibilitychange", visibility);
        window.removeEventListener("pagehide", pageHide);
        observer.disconnect();
    };
    const pageHide = () => {
        cleanup();
        window.addEventListener("pageshow", event => {
            if (event.persisted) initializeScreenPreview();
        }, { once: true });
    };
    const refresh = async () => {
        if (stopped || document.hidden || !image.isConnected) return;
        if (busy) { refreshAgain = true; return; }
        busy = true;
        refreshAgain = false;
        const version = revision;
        const started = performance.now();
        controller = new AbortController();
        const timeout = setTimeout(() => controller?.abort(), 5000);
        let pendingUrl;
        try {
            const response = await fetch("/api/screen-preview", {
                credentials: "same-origin", cache: "no-store",
                headers: { Accept: "image/jpeg" }, signal: controller.signal
            });
            if (version !== revision || stopped) return;
            if (response.status === 401 || response.status === 403 || response.redirected) {
                clearImage();
                status.textContent = t("Bitte erneut anmelden, um die Vorschau zu sehen.");
                stopped = true;
                return;
            }
            if (response.status === 503) {
                const error = await response.json();
                if (version !== revision || stopped) return;
                clearImage();
                status.textContent = t(error.error === "monitor_unavailable"
                    ? "Der konfigurierte Beamer-Monitor ist nicht verfügbar." : "Die Bildschirmaufnahme ist nicht verfügbar.");
                return;
            }
            if (!response.ok || !response.headers.get("Content-Type")?.startsWith("image/jpeg"))
                throw new Error("Screenshot unavailable");
            const captureTime = response.headers.get("X-Captured-At-Utc");
            const captured = new Date(captureTime);
            if (!captureTime || Number.isNaN(captured.getTime())) throw new Error("Missing capture time");
            pendingUrl = URL.createObjectURL(await response.blob());
            const next = new Image();
            next.src = pendingUrl;
            await next.decode();
            if (version !== revision || stopped || document.hidden || !image.isConnected) return;
            const oldUrl = currentUrl;
            image.src = pendingUrl;
            currentUrl = pendingUrl;
            pendingUrl = undefined;
            image.hidden = false;
            if (oldUrl) URL.revokeObjectURL(oldUrl);
            timestamp.dateTime = captured.toISOString();
            timestamp.textContent = t("Aufnahme: {0}").replace("{0}", captured.toLocaleString());
            timestamp.hidden = false;
            status.textContent = t("Vorschau aktuell");
        } catch {
            if (version === revision && !stopped)
                status.textContent = t(currentUrl ? "Verbindung unterbrochen – letztes Bild ist veraltet." : "Vorschau nicht erreichbar. Neuer Versuch …");
        } finally {
            if (pendingUrl) URL.revokeObjectURL(pendingUrl);
            clearTimeout(timeout);
            controller = undefined;
            busy = false;
            if (!stopped && !document.hidden && image.isConnected)
                timer = setTimeout(refresh, refreshAgain ? 0 : Math.max(0, Number(mode.value) - (performance.now() - started)));
        }
    };
    const restart = () => {
        revision++;
        clearTimeout(timer);
        controller?.abort();
        refresh();
    };
    const visibility = () => {
        if (document.hidden) {
            revision++;
            refreshAgain = false;
            clearTimeout(timer);
            controller?.abort();
        } else restart();
    };
    const observer = new MutationObserver(() => { if (!image.isConnected) cleanup(); });
    observer.observe(document.body, { childList: true, subtree: true });
    mode.addEventListener("change", restart);
    document.addEventListener("visibilitychange", visibility);
    window.addEventListener("pagehide", pageHide);
    window.presenterScreenPreviewCleanup = cleanup;
    refresh();
})();

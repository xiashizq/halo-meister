const state = {
  token: "",
  weapons: [],
  enemies: [],
  skulls: [],
  loaded: new Set(),
  strings: {},
  language: "en",
};

const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];

function t(key, ...args) {
  let value = state.strings[key] || key;
  if (args.length) {
    value = value.replace(/\{(\d+)\}/g, (_, index) => {
      const item = args[Number(index)];
      return item === undefined || item === null ? "" : String(item);
    });
  }
  return value;
}

function normalizeLanguage(value) {
  const raw = String(value || "").trim().replace("_", "-");
  if (!raw) return "en";
  if (/^zh/i.test(raw)) return "zh-Hans";
  if (/^ja/i.test(raw)) return "ja";
  if (/^ko/i.test(raw)) return "ko";
  if (["en", "zh-Hans", "ja", "ko"].includes(raw)) return raw;
  return "en";
}

function detectLanguage() {
  const url = new URL(window.location.href);
  const fromQuery = url.searchParams.get("lang");
  if (fromQuery) return normalizeLanguage(fromQuery);
  const saved = localStorage.getItem("haloMeisterLang");
  if (saved) return normalizeLanguage(saved);
  return normalizeLanguage(navigator.language || "en");
}

async function loadStrings() {
  state.language = detectLanguage();
  localStorage.setItem("haloMeisterLang", state.language);
  document.documentElement.lang = state.language === "zh-Hans" ? "zh-Hans" : state.language;
  try {
    const response = await fetch(`/i18n/${state.language}.json`, { cache: "no-store" });
    if (!response.ok) throw new Error(`locale ${response.status}`);
    state.strings = await response.json();
  } catch {
    if (state.language !== "en") {
      const fallback = await fetch("/i18n/en.json", { cache: "no-store" });
      state.strings = fallback.ok ? await fallback.json() : {};
    } else {
      state.strings = {};
    }
  }
  applyStaticTranslations();
}

function applyStaticTranslations() {
  $$("[data-i18n]").forEach((element) => {
    const key = element.getAttribute("data-i18n");
    if (key) element.textContent = t(key);
  });
  $$("[data-i18n-placeholder]").forEach((element) => {
    const key = element.getAttribute("data-i18n-placeholder");
    if (key) element.setAttribute("placeholder", t(key));
  });
  $$("[data-i18n-aria]").forEach((element) => {
    const key = element.getAttribute("data-i18n-aria");
    if (key) element.setAttribute("aria-label", t(key));
  });
}

function capturePairingToken() {
  const url = new URL(window.location.href);
  const paired = url.searchParams.get("pair");
  if (paired) {
    localStorage.setItem("haloMeisterPairing", paired);
    url.searchParams.delete("pair");
    history.replaceState({}, "", `${url.pathname}${url.search}${url.hash}`);
  }
  state.token = localStorage.getItem("haloMeisterPairing") || "";
  $("#pairing-required").classList.toggle("hidden", Boolean(state.token));
  $("#home-view").classList.toggle("hidden", !state.token);
  $(".bottom-nav").classList.toggle("hidden", !state.token);
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Authorization", `Bearer ${state.token}`);
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...options, headers });
  const contentType = response.headers.get("content-type") || "";
  const payload = contentType.includes("json") ? await response.json() : {};
  if (!response.ok) {
    if (response.status === 401) {
      localStorage.removeItem("haloMeisterPairing");
      state.token = "";
      $("#pairing-required").classList.remove("hidden");
    }
    throw new Error(payload.detail || payload.error || t("remote.request_failed", response.status));
  }
  return payload;
}

let toastTimer;
function toast(message, error = false) {
  const element = $("#toast");
  element.textContent = message;
  element.classList.toggle("error", error);
  element.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.remove("show"), 3200);
}

function busy(button, isBusy, label) {
  if (!button) return;
  const busyLabel = label || t("remote.working");
  if (isBusy) {
    button.dataset.label = button.textContent;
    button.textContent = busyLabel;
    button.disabled = true;
  } else {
    button.textContent = button.dataset.label || button.textContent;
    button.disabled = false;
  }
}

function showView(name) {
  $$(".view").forEach(view => view.classList.toggle("active", view.id === `${name}-view`));
  $$(".bottom-nav button").forEach(button => button.classList.toggle("active", button.dataset.view === name));
  window.scrollTo({ top: 0, behavior: "smooth" });
  if (name !== "home" && !state.loaded.has(name)) loadView(name);
}

async function refreshStatus() {
  if (!state.token) return;
  try {
    const result = await api("/api/status");
    const ready = result.gameConnected && result.bridgeReady;
    $("#status-pill").className = `status-pill ${ready ? "online" : result.gameConnected ? "warning" : "offline"}`;
    $("#status-pill b").textContent = ready
      ? t("remote.ready")
      : result.gameConnected
        ? t("remote.bridge")
        : t("remote.disconnected");
    $("#game-state").textContent = result.gameConnected
      ? t("remote.ready")
      : t("remote.not_connected");
    $("#game-detail").textContent = result.gameConnected
      ? `PID ${result.processId}`
      : t("remote.tap_to_connect");
    $("#bridge-state").textContent = result.bridgeReady ? t("remote.ready") : t("remote.not_ready");
    $("#bridge-detail").textContent = result.bridgeVersion
      ? t("remote.version", result.bridgeVersion)
      : t("remote.check_desktop");
    $("#hero-detail").textContent = ready
      ? t("remote.ready_detail")
      : (result.bridgeSummary || t("remote.hero_checking"));
    $("#connect-button").classList.toggle("hidden", result.gameConnected);
  } catch (error) {
    $("#status-pill").className = "status-pill offline";
    $("#status-pill b").textContent = t("remote.pc_unavailable");
    $("#hero-detail").textContent = error.message;
  }
}

async function connectGame(event) {
  const button = event.currentTarget;
  busy(button, true, t("remote.connecting"));
  try {
    const result = await api("/api/connect", { method: "POST" });
    toast(result.message);
    state.loaded.clear();
    await refreshStatus();
  } catch (error) {
    toast(error.message, true);
  } finally {
    busy(button, false);
  }
}

function filtered(items, query, fields) {
  const value = query.trim().toLowerCase();
  if (!value) return items;
  return items.filter(item => fields.some(field => String(item[field] || "").toLowerCase().includes(value)));
}

function renderWeapons() {
  const list = $("#weapon-list");
  const items = filtered(state.weapons, $("#weapon-search").value, ["name", "path"]);
  list.classList.remove("loading-block");
  list.innerHTML = items.length ? items.map(weapon => `
    <article class="list-card">
      <div class="copy"><b>${escapeHtml(weapon.name)}</b><small>${escapeHtml(weapon.path)}</small></div>
      <button class="list-action weapon-load" data-id="${weapon.id}" type="button">${escapeHtml(t("remote.load"))}</button>
    </article>`).join("") : `<div class="loading-block">${escapeHtml(t("remote.no_weapons"))}</div>`;
  $$(".weapon-load").forEach(button => button.addEventListener("click", loadWeapon));
}

async function loadWeapons() {
  const list = $("#weapon-list");
  list.className = "card-list loading-block";
  list.textContent = t("remote.scanning_mission");
  try {
    state.weapons = await api("/api/weapons");
    state.loaded.add("weapons");
    renderWeapons();
  } catch (error) {
    list.textContent = error.message;
    toast(error.message, true);
  }
}

async function loadWeapon(event) {
  const button = event.currentTarget;
  busy(button, true);
  try {
    const result = await api(`/api/weapons/${button.dataset.id}/load`, { method: "POST" });
    toast(result.message || t("remote.weapon_loaded"));
  } catch (error) {
    toast(error.message, true);
  } finally {
    busy(button, false);
  }
}

function renderEnemies() {
  const list = $("#enemy-list");
  const items = filtered(state.enemies, $("#enemy-search").value, ["name", "path", "category"]);
  list.classList.remove("loading-block");
  list.innerHTML = items.length ? items.map(enemy => `
    <article class="list-card enemy-card">
      <div class="copy"><b>${escapeHtml(enemy.name)}</b><small>${escapeHtml(enemy.category)} · ${escapeHtml(enemy.path)}</small></div>
      <div class="controls">
        <div class="variant-row">
          <select aria-label="${escapeHtml(t("remote.enemy_variant"))}" data-variants="${enemy.id}">
            ${enemy.variants.map(variant => `<option value="${variant.id}">${escapeHtml(variant.name)}</option>`).join("")}
          </select>
        </div>
        <div class="spawn-actions">
          <button class="list-action enemy-spawn" data-id="${enemy.id}" data-mode="single" type="button">${escapeHtml(t("remote.spawn"))}</button>
          <button class="list-action enemy-spawn" data-id="${enemy.id}" data-mode="team" type="button">${escapeHtml(t("remote.team"))}</button>
        </div>
      </div>
    </article>`).join("") : `<div class="loading-block">${escapeHtml(t("remote.no_enemies"))}</div>`;
  $$(".enemy-spawn").forEach(button => button.addEventListener("click", spawnEnemy));
}

async function loadEnemies() {
  const list = $("#enemy-list");
  list.className = "card-list loading-block";
  list.textContent = t("remote.reading_characters");
  try {
    state.enemies = await api("/api/enemies");
    state.loaded.add("spawner");
    renderEnemies();
  } catch (error) {
    list.textContent = error.message;
    toast(error.message, true);
  }
}

async function spawnEnemy(event) {
  const button = event.currentTarget;
  const select = document.querySelector(`select[data-variants="${button.dataset.id}"]`);
  busy(button, true, t("remote.spawning"));
  try {
    const result = await api(`/api/enemies/${button.dataset.id}/spawn`, {
      method: "POST",
      body: JSON.stringify({ variantId: Number(select.value), mode: button.dataset.mode }),
    });
    toast(result.message || t("remote.spawn_confirmed"));
  } catch (error) {
    toast(error.message, true);
  } finally {
    busy(button, false);
  }
}

function renderSkulls() {
  const list = $("#skull-list");
  const items = filtered(state.skulls, $("#skull-search").value, ["name", "id"]);
  list.classList.remove("loading-block");
  list.innerHTML = items.length ? items.map(skull => `
    <article class="list-card">
      <div class="copy"><b>${escapeHtml(skull.name)}</b><small>${escapeHtml(skull.id)}</small></div>
      <label class="switch">
        <input class="skull-toggle" data-id="${escapeHtml(skull.id)}" type="checkbox" ${skull.enabled ? "checked" : ""}>
        <span></span>
      </label>
    </article>`).join("") : `<div class="loading-block">${escapeHtml(t("remote.no_skulls"))}</div>`;
  $$(".skull-toggle").forEach(toggle => toggle.addEventListener("change", setSkull));
}

async function loadSkulls() {
  const list = $("#skull-list");
  list.className = "card-list loading-block";
  list.textContent = t("remote.reading_skulls");
  try {
    state.skulls = await api("/api/skulls");
    state.loaded.add("skulls");
    renderSkulls();
  } catch (error) {
    list.textContent = error.message;
    toast(error.message, true);
  }
}

async function setSkull(event) {
  const toggle = event.currentTarget;
  const previous = !toggle.checked;
  toggle.disabled = true;
  try {
    const result = await api(`/api/skulls/${encodeURIComponent(toggle.dataset.id)}`, {
      method: "PUT",
      body: JSON.stringify({ enabled: toggle.checked }),
    });
    const item = state.skulls.find(skull => skull.id === toggle.dataset.id);
    if (item) item.enabled = toggle.checked;
    toast(result.message);
  } catch (error) {
    toggle.checked = previous;
    toast(error.message, true);
  } finally {
    toggle.disabled = false;
  }
}

async function readPosition(event) {
  const button = event.currentTarget;
  busy(button, true, t("remote.reading"));
  try {
    const position = await api("/api/player");
    $("#coord-x").value = position.x;
    $("#coord-y").value = position.y;
    $("#coord-z").value = position.z;
    state.loaded.add("player");
    toast(t("remote.position_captured"));
  } catch (error) {
    toast(error.message, true);
  } finally {
    busy(button, false);
  }
}

async function teleport(event) {
  const button = event.currentTarget;
  const body = {
    x: Number($("#coord-x").value),
    y: Number($("#coord-y").value),
    z: Number($("#coord-z").value),
  };
  if (![body.x, body.y, body.z].every(Number.isFinite)) {
    toast(t("remote.invalid_coords"), true);
    return;
  }
  busy(button, true, t("remote.teleporting"));
  try {
    const result = await api("/api/player/teleport", { method: "POST", body: JSON.stringify(body) });
    toast(result.message);
  } catch (error) {
    toast(error.message, true);
  } finally {
    busy(button, false);
  }
}

function loadView(name) {
  if (name === "weapons") loadWeapons();
  if (name === "spawner") loadEnemies();
  if (name === "skulls") loadSkulls();
  if (name === "player") readPosition({ currentTarget: $("#read-position") });
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

async function boot() {
  await loadStrings();
  capturePairingToken();
  $$(".bottom-nav button").forEach(button => button.addEventListener("click", () => showView(button.dataset.view)));
  $$("[data-go]").forEach(button => button.addEventListener("click", () => showView(button.dataset.go)));
  $$(".refresh-view").forEach(button => button.addEventListener("click", () => loadView(button.dataset.view)));
  $("#connect-button").addEventListener("click", connectGame);
  $("#weapon-search").addEventListener("input", renderWeapons);
  $("#enemy-search").addEventListener("input", renderEnemies);
  $("#skull-search").addEventListener("input", renderSkulls);
  $("#read-position").addEventListener("click", readPosition);
  $("#teleport-button").addEventListener("click", teleport);
  $("#status-pill").addEventListener("click", refreshStatus);
  refreshStatus();
  setInterval(refreshStatus, 5000);
}

boot();

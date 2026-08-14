// ============================================================
// Shared shell + helpers for every authenticated app page
// (dashboard, flights, airports, routes, bookings, tickets,
// passengers, payments, baggage, seatclasses, airplanes, crew,
// users). Each of those pages has its own <html> file and its
// own js/pages/<name>.js with just that entity's load/render/
// filter/edit/delete logic - this file is everything they all
// need in common, so it isn't copy-pasted 13 times:
//   - requireLogin guard + sidebar user info + role-based nav hiding
//   - active nav-item highlighting (based on the current filename)
//   - sidebar collapse toggle
//   - the alert dismiss button wiring (api.js's showAlert markup
//     uses data-bs-dismiss, which needs Bootstrap's JS - these pages
//     don't load Bootstrap, so it's wired up here instead)
//   - the API health-check pill
//   - patchStatus() - the shared PATCH helper for every state-machine
//     endpoint (flight/booking/payment status, user role)
//   - buildQuery / clearFilter / loadStats / renderStatsValue /
//     formatStatCell / humanizeKey - the filter+stats helpers used
//     by all 12 CRUD pages' "case 7 / case 8" UI
//
// depends on api.js being loaded first (auth, api, showAlert,
// escapeHtml, formatDate, formatMoney, statusBadge, requireLogin)
// and auth.js for logout(). include both before this file.
// ============================================================

// which nav items are hidden for which roles. cosmetic only - the
// backend still enforces [Authorize(Roles=...)] on every endpoint
// regardless of whether the link is visible, this just stops
// someone clicking into a page that's guaranteed to 403 for them
const PAGE_ROLES = {
  passengers: ["Admin", "Staff"],
  payments: ["Admin", "Staff"],
  baggage: ["Admin", "Staff"],
  airplanes: ["Admin", "Staff"],
  crew: ["Admin", "Staff"],
  // FlightCrewsController is Admin,Staff on all 8 endpoints
  flightcrew: ["Admin", "Staff"],
  users: ["Admin"]
};

// role helpers. pages use these to show only the actions the caller can
// actually perform - a button that's guaranteed to come back 403 is worse
// than no button. cosmetic only, exactly like PAGE_ROLES above: the server
// enforces every one of these rules regardless of what the UI renders
function isStaffUser() {
  const me = auth.getUser();
  return !!me && (me.role === "Admin" || me.role === "Staff");
}

function isAdminUser() {
  const me = auth.getUser();
  return !!me && me.role === "Admin";
}


// ---------- mobile drawer ----------
// under 900px the sidebar is positioned off-canvas by CSS and this slides it
// back in. `body.drawer-open` is what actually drives the CSS rather than a
// class on the sidebar itself: the body always exists, so there is no way for
// the toggle to silently no-op on a page whose markup differs, and one class
// drives both the panel and the scrim so they can never disagree.
function wireMobileDrawer() {
  const burger = document.getElementById("hamburger");
  if (!burger) return;

  const isOpen = () => document.body.classList.contains("drawer-open");

  const setDrawer = (open) => {
    document.body.classList.toggle("drawer-open", open);
    burger.setAttribute("aria-expanded", String(open));
  };

  burger.addEventListener("click", (e) => {
    // stopPropagation matters: the scrim becomes interactive the instant the
    // drawer opens, and without this the same gesture can read as a tap
    // outside and close it again
    e.preventDefault();
    e.stopPropagation();
    setDrawer(!isOpen());
  });

  // resolved at click time rather than captured at init, so this survives any
  // page that renders its shell later
  document.addEventListener("click", (e) => {
    if (!isOpen()) return;
    if (e.target.closest("#sidebarScrim")) setDrawer(false);
    // following a nav link should navigate AND put the drawer away
    if (e.target.closest(".nav-item")) setDrawer(false);
  });

  document.addEventListener("keydown", e => { if (e.key === "Escape") setDrawer(false); });

  // leaving mobile width with the drawer open would otherwise strand the
  // body class and pin the sidebar over the content on desktop
  window.matchMedia("(min-width: 901px)").addEventListener("change", (m) => {
    if (m.matches) setDrawer(false);
  });
}


// ---------- booking flow ----------
// a booking is only useful once it has a flight on it and has been paid for,
// but those are three separate pages. this renders the "you are here" bar so
// someone mid-journey can see what's left instead of being dropped back on a
// list with a 0.00 booking and no idea what to do next.
// `step` is 1 = booking made, 2 = choosing flights/seats, 3 = payment
function renderFlowBar(mountId, step, bookingId, { total = null } = {}) {
  const mount = document.getElementById(mountId);
  if (!mount) return;

  const steps = [
    { n: 1, label: "Booking created" },
    { n: 2, label: "Add flights & seats" },
    { n: 3, label: "Pay" }
  ];

  const cls = s => s.n < step ? "is-done" : s.n === step ? "is-active" : "";
  const dot = s => s.n < step ? "✓" : s.n;

  const next = step === 2
    ? `<a class="btn" href="bookings.html?pay=${bookingId}">
         ${total ? `Continue to payment · ${formatMoney(total)}` : "Continue to payment"}
       </a>`
    : "";

  mount.innerHTML = `
    <div class="flow-bar">
      <div class="flow-steps">
        ${steps.map((s, i) =>
          `${i ? `<span class="flow-sep">›</span>` : ""}
           <span class="flow-step ${cls(s)}">
             <span class="flow-dot">${dot(s)}</span>${s.label}
           </span>`).join("")}
      </div>
      <div class="flow-actions">
        <span class="cell-dim" style="align-self:center;">Booking #${bookingId}</span>
        ${next}
      </div>
    </div>`;
}


// ---------- client-side pagination ----------
// most list endpoints return the whole table in one go (only /flights
// paginates server-side), so this slices the array the page already has.
// that's the right trade at this data volume - it keeps filtering and
// sorting instant, and there's no round trip to change page.
//
// usage:
//   const pager = createPager({ pageSize: 10, onRender: renderRoutesTable });
//   pager.setData(rows);                     // after every load/filter
//   <tbody id="x"></tbody><div id="routesPager"></div>
function createPager({ pageSize = 10, onRender, mountId }) {
  let rows = [];
  let page = 1;
  let size = pageSize;

  function totalPages() {
    return Math.max(1, Math.ceil(rows.length / size));
  }

  function slice() {
    const start = (page - 1) * size;
    return rows.slice(start, start + size);
  }

  // 1 … 4 5 [6] 7 8 … 20 - keeps the control a fixed width however many
  // pages there are, instead of rendering 200 buttons
  function pageNumbers() {
    const last = totalPages();
    const out = new Set([1, last, page, page - 1, page + 1]);
    const nums = [...out].filter(n => n >= 1 && n <= last).sort((a, b) => a - b);
    const withGaps = [];
    nums.forEach((n, i) => {
      if (i && n - nums[i - 1] > 1) withGaps.push("…");
      withGaps.push(n);
    });
    return withGaps;
  }

  function renderControls() {
    const mount = document.getElementById(mountId);
    if (!mount) return;

    if (!rows.length) { mount.innerHTML = ""; return; }

    const last = totalPages();
    const from = (page - 1) * size + 1;
    const to = Math.min(page * size, rows.length);

    mount.innerHTML = `
      <div class="pager">
        <div class="pager-info">Showing <b>${from}–${to}</b> of <b>${rows.length}</b></div>
        <div class="pager-controls">
          <button class="pager-btn" data-goto="prev" ${page === 1 ? "disabled" : ""}>‹</button>
          ${pageNumbers().map(n => n === "…"
            ? `<span class="pager-info">…</span>`
            : `<button class="pager-btn ${n === page ? "is-current" : ""}" data-goto="${n}">${n}</button>`
          ).join("")}
          <button class="pager-btn" data-goto="next" ${page === last ? "disabled" : ""}>›</button>
        </div>
        <label class="pager-size">Per page
          <select class="form-control" data-pagesize>
            ${[10, 25, 50].map(n => `<option value="${n}" ${n === size ? "selected" : ""}>${n}</option>`).join("")}
          </select>
        </label>
      </div>`;

    mount.querySelectorAll("[data-goto]").forEach(btn =>
      btn.addEventListener("click", () => {
        const v = btn.dataset.goto;
        page = v === "prev" ? Math.max(1, page - 1)
             : v === "next" ? Math.min(last, page + 1)
             : Number(v);
        draw();
      }));

    mount.querySelector("[data-pagesize]")?.addEventListener("change", e => {
      size = Number(e.target.value);
      page = 1;
      draw();
    });
  }

  function draw() {
    onRender(slice());
    renderControls();
  }

  return {
    setData(next) { rows = next || []; page = 1; draw(); },
    refresh: draw
  };
}


// call once at the top of every app page's DOMContentLoaded handler,
// passing the page's own key (matches the nav-item's data-page and
// the PAGE_ROLES keys above). returns false if the user got bounced
// to the login page, so the caller can bail out early
function initShell(pageKey) {
  if (!requireLogin()) return false;

  const me = auth.getUser();
  if (me) {
    const avatarEl = document.getElementById("sidebarAvatar");
    const nameEl = document.getElementById("sidebarName");
    const roleEl = document.getElementById("sidebarRole");
    if (avatarEl) avatarEl.textContent = me.name ? me.name.charAt(0).toUpperCase() : "?";
    if (nameEl) nameEl.textContent = me.name || "—";
    if (roleEl) roleEl.textContent = me.role || "—";

    document.querySelectorAll(".nav-item").forEach(item => {
      const allowed = PAGE_ROLES[item.dataset.page];
      if (allowed && !allowed.includes(me.role)) item.style.display = "none";
    });

    // declarative gating for anything else on the page:
    //   <div class="form-panel" data-requires-role="Admin,Staff">
    // a passenger could previously see the "Add flight" / "Add airport" forms
    // and the Edit/Delete buttons on every reference-data page. pressing them
    // just produced a red 403 banner, which reads like the app is broken
    // rather than like the button was never theirs to press
    document.querySelectorAll("[data-requires-role]").forEach(el => {
      const allowed = el.dataset.requiresRole.split(",").map(r => r.trim());
      if (!allowed.includes(me.role)) el.remove();
    });
  }

  document.querySelectorAll(".nav-item").forEach(item => {
    item.classList.toggle("active", item.dataset.page === pageKey);
  });

  const collapseBtn = document.getElementById("collapseBtn");
  if (collapseBtn) {
    collapseBtn.addEventListener("click", () => {
      document.getElementById("sidebar").classList.toggle("collapsed");
    });
  }

  wireMobileDrawer();

  const alertBox = document.getElementById("alert");
  if (alertBox) {
    alertBox.addEventListener("click", (e) => {
      if (e.target.closest(".btn-close")) alertBox.innerHTML = "";
    });
  }

  checkApiStatus();
  return true;
}

/* ============================================================
   API HEALTH CHECK
   the "Connected" pill used to be hardcoded and never actually
   checked anything. this fires one lightweight GET on load and
   flips it red on failure
   ============================================================ */
async function checkApiStatus() {
  const pill = document.getElementById("apiStatusPill");
  if (!pill) return;
  try {
    await api.get("/airports");
    pill.className = "pill pill-api";
    pill.innerHTML = '<span class="pill-dot"></span>Connected';
  } catch (err) {
    pill.className = "pill pill-api-down";
    pill.innerHTML = '<span class="pill-dot"></span>API unreachable';
  }
}

// PATCH helper for the state-machine endpoints (flight/booking/payment
// status, user role). optional confirm text since some of these are
// destructive-ish
async function patchStatus(path, body, reloadFns, confirmText) {
  if (confirmText && !await uiConfirm(confirmText, { title: "Please confirm" })) return;
  try {
    await api.patch(path, body);
    showAlert("Updated successfully", "success");
    reloadFns.forEach(fn => fn());
  } catch (err) { showAlert(err.message); }
}

/* ============================================================
   FILTER / STATS HELPERS
   every controller implements GET /filter and GET /stats (cases 7
   and 8 of the CRUD spec) - these helpers are what let each page
   wire those up in a few lines instead of hand-building query
   strings and bespoke stats markup twelve separate times
   ============================================================ */

// reads a {queryParam: elementId} map, skips anything blank/unset,
// returns a query string with no leading "?". works for text/number/
// date inputs and checkboxes alike
function buildQuery(fieldMap) {
  const params = new URLSearchParams();
  for (const [key, id] of Object.entries(fieldMap)) {
    const el = document.getElementById(id);
    if (!el) continue;
    const value = el.type === "checkbox" ? el.checked : el.value;
    if (value === "" || value === false || value === null || value === undefined) continue;
    params.set(key, value);
  }
  return params.toString();
}

// resets a filter form's fields and reloads the unfiltered list
function clearFilter(ids, reloadFn) {
  ids.forEach(id => {
    const el = document.getElementById(id);
    if (!el) return;
    if (el.type === "checkbox") el.checked = false; else el.value = "";
  });
  reloadFn();
}

// every /stats endpoint returns a DIFFERENT shape - some a flat array
// (SeatClasses, Tickets), some an object with several named sections
// (Airports: byCountry / busiestHubs / unusedAirports; Bookings:
// byStatus / topCustomers / integrityCheck). rather than hand-build
// bespoke markup for each of the 12, this walks whatever comes back:
// objects become labelled sections, arrays of objects become mini
// tables, arrays of scalars become a comma list, plain values are
// shown as-is
async function loadStats(path, containerId) {
  const box = document.getElementById(containerId);
  box.innerHTML = '<span class="cell-dim">Loading…</span>';
  try {
    const data = await api.get(path);
    box.innerHTML = renderStatsValue(data);
  } catch (err) {
    box.innerHTML = "";
    showAlert(err.message);
  }
}
function renderStatsValue(val) {
  if (val === null || val === undefined) return '<span class="cell-dim">—</span>';
  if (Array.isArray(val)) {
    if (val.length === 0) return '<span class="cell-dim">No rows.</span>';
    if (typeof val[0] === "object" && val[0] !== null) {
      const cols = Object.keys(val[0]);
      return `<table><thead><tr>${cols.map(c => `<th>${escapeHtml(humanizeKey(c))}</th>`).join("")}</tr></thead>
        <tbody>${val.map(row => `<tr>${cols.map(c => `<td>${formatStatCell(row[c])}</td>`).join("")}</tr>`).join("")}</tbody></table>`;
    }
    return escapeHtml(val.join(", "));
  }
  if (typeof val === "object") {
    return Object.entries(val).map(([key, v]) => `
      <div class="stat-section">
        <h4>${escapeHtml(humanizeKey(key))}</h4>
        ${renderStatsValue(v)}
      </div>
    `).join("");
  }
  return escapeHtml(String(val));
}
function formatStatCell(v) {
  if (v === null || v === undefined) return '<span class="cell-dim">—</span>';
  if (typeof v === "object") return renderStatsValue(v);
  if (typeof v === "boolean") return v ? "Yes" : "No";
  return escapeHtml(String(v));
}
// "TotalBookings" -> "Total Bookings", "byStatus" -> "By Status"
function humanizeKey(key) {
  return key.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, s => s.toUpperCase()).trim();
}

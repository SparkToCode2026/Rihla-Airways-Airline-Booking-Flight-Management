// Dashboard page - stat cards, live flight board, recent bookings.
// Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("dashboard")) return;

  const today = new Date();
  document.getElementById("todayDate").textContent =
    today.toLocaleDateString("en-US", { weekday: "long", day: "numeric", month: "long", year: "numeric" }) +
    " — Live dashboard";

  loadDashboard();
});

// these used to be .catch(() => []), which turned a 403 into an empty array -
// so a Passenger's dashboard confidently reported "Registered Airplanes 0" and
// "System Users 0" when the real answer was "you're not allowed to see this".
// a wrong number is worse than no number, so keep the failure distinguishable
async function tryGet(path, empty) {
  try {
    return { ok: true, data: await api.get(path) };
  } catch (err) {
    return { ok: false, forbidden: err.status === 403 || err.status === 401, data: empty };
  }
}

// "—" for a stat this role can't see, and the card is dimmed so it reads as
// unavailable rather than as a real zero
function setStat(valueId, cardId, footId, result, count, footText) {
  const el = document.getElementById(valueId);
  const card = cardId ? document.getElementById(cardId) : null;
  const foot = footId ? document.getElementById(footId) : null;

  if (result.ok) {
    el.textContent = count;
    if (foot && footText) foot.textContent = footText;
    if (card) card.classList.remove("stat-unavailable");
    return;
  }

  el.textContent = "—";
  if (foot) foot.textContent = result.forbidden ? "not available for your role" : "could not load";
  if (card) card.classList.add("stat-unavailable");
}

async function loadDashboard() {
  try {
    const me = auth.getUser() || {};
    const isStaff = me.role === "Admin" || me.role === "Staff";

    // /flights is paginated - { total, page, pageSize, items } - not a bare
    // array like every other list endpoint. unwrap it here
    const [flightsRes, bookingsRes, airplanesRes, usersRes] = await Promise.all([
      tryGet("/flights?pageSize=200", { total: 0, items: [] }),
      tryGet("/bookings", []),
      tryGet("/airplanes", []),
      tryGet("/users", [])
    ]);

    const flightsPage = flightsRes.data;
    const flights = flightsPage.items || [];
    const bookings = bookingsRes.data;

    setStat("statFlights", null, null, flightsRes, flightsPage.total ?? flights.length);
    // /bookings scopes to the caller, so the label has to follow the role
    setStat("statBookings", null, "statBookingsFoot", bookingsRes, bookings.length,
            isStaff ? "system-wide" : "your bookings");
    setStat("statAirplanes", "cardAirplanes", "statAirplanesFoot", airplanesRes,
            airplanesRes.data.length, "fleet count");
    setStat("statUsers", "cardUsers", "statUsersFoot", usersRes,
            usersRes.data.length, "accounts active");

    // the two widgets are role-shaped: a passenger cares about their own next
    // trip and what still needs paying; ops cares about today's flying and
    // which bookings are stuck
    renderWidgets({ isStaff, flights, bookings });

    // FlightResponseDto already carries originCode/destinationCode/
    // airplaneModel - no reason to show a bare FK id when the readable
    // value is sitting right there in the response
    document.getElementById("dashFlightBoard").innerHTML = flights.slice(0, 5).map(f => `
      <tr>
        <td class="mono-link">${escapeHtml(f.flightNumber)}</td>
        <td>${escapeHtml(f.originCode)} → ${escapeHtml(f.destinationCode)}</td>
        <td>${formatDate(f.departureTime)}</td>
        <td>${escapeHtml(f.airplaneModel)}</td>
        <td>${statusBadge(f.status)}</td>
      </tr>
    `).join("");

    document.getElementById("dashBookings").innerHTML = bookings.slice(0, 5).map(b => `
      <div class="booking-row">
        <div class="avatar">${escapeHtml((b.customerName || "U").charAt(0).toUpperCase())}</div>
        <div class="booking-info">
          <div class="booking-name">${escapeHtml(b.customerName)}</div>
          <div class="booking-meta">Booking #${b.id} · ${formatDate(b.bookingDate)}</div>
        </div>
        <div class="booking-right">
          <div class="booking-amount">${formatMoney(b.totalAmount)}</div>
          ${statusBadge(b.status)}
        </div>
      </div>
    `).join("");
  } catch (err) {
    if (err.status !== 403) showAlert(err.message);
  }
}


// ============================================================
// WIDGETS
// ============================================================

async function renderWidgets({ isStaff, flights, bookings }) {
  if (isStaff) {
    renderOpsToday(flights);
    renderOpsAttention(flights, bookings);
  } else {
    await renderNextTrip();
    renderPassengerAttention(bookings);
  }
}


// ---------- passenger: next trip ----------
// the single most useful thing on a customer's dashboard - when am I flying,
// from where, and in which seat. built from their own tickets, which are
// already scoped server-side
async function renderNextTrip() {
  const box = document.getElementById("widgetPrimary");
  let tickets = [];
  try { tickets = await api.get("/tickets"); } catch { /* leave it empty */ }

  const now = new Date();
  const upcoming = tickets
    .filter(t => new Date(t.departureTime) > now)
    .sort((a, b) => new Date(a.departureTime) - new Date(b.departureTime));

  if (!upcoming.length) {
    box.innerHTML = `
      <div class="next-trip">
        <div class="stat-label">Next trip</div>
        <div class="next-trip-meta" style="margin-top:10px;">
          Nothing booked yet. <a href="bookings.html">Start a booking →</a>
        </div>
      </div>`;
    return;
  }

  const t = upcoming[0];
  const dep = new Date(t.departureTime);
  const days = Math.ceil((dep - now) / 86400000);

  box.innerHTML = `
    <div class="next-trip">
      <div class="stat-label">Your next trip</div>
      <div class="next-trip-route">
        <span class="next-trip-code">${escapeHtml(t.originCode)}</span>
        <span class="next-trip-arrow">✈</span>
        <span class="next-trip-code">${escapeHtml(t.destinationCode)}</span>
      </div>
      <div class="next-trip-meta">
        ${escapeHtml(t.flightNumber)} · ${formatDate(t.departureTime)}
        · seat <b>${escapeHtml(t.seatNumber)}</b> (${escapeHtml(t.seatClassName)})
      </div>
      <span class="next-trip-countdown">
        ${days <= 0 ? "Departing today" : days === 1 ? "Tomorrow" : `In ${days} days`}
      </span>
    </div>`;
}


// ---------- passenger: what still needs doing ----------
function renderPassengerAttention(bookings) {
  const box = document.getElementById("widgetAttention");
  const rows = [];

  const unpaid = bookings.filter(b => b.status !== "Cancelled" && !b.isPaid && b.totalAmount > 0);
  const empty = bookings.filter(b => b.status !== "Cancelled" && b.totalAmount === 0);

  unpaid.forEach(b => rows.push({
    cls: "attention-warn", icon: "!",
    title: `Booking #${b.id} is unpaid`,
    sub: `${formatMoney(b.totalAmount)} outstanding`,
    href: `bookings.html?pay=${b.id}`, action: "Pay now"
  }));

  empty.forEach(b => rows.push({
    cls: "attention-warn", icon: "+",
    title: `Booking #${b.id} has no flights`,
    sub: "Add a flight or cancel it",
    href: `tickets.html?bookingId=${b.id}&flow=1`, action: "Add flight"
  }));

  paint(box, rows, "You're all set — nothing needs your attention.");
}


// ---------- staff: today's operation ----------
function renderOpsToday(flights) {
  const box = document.getElementById("widgetPrimary");
  const today = new Date().toDateString();
  const todays = flights.filter(f => new Date(f.departureTime).toDateString() === today);

  // a status breakdown is more use to ops than a single count - it says
  // where the day's problems are
  const order = ["Scheduled", "Boarding", "Departed", "Landed", "Delayed", "Cancelled"];
  const counts = {};
  todays.forEach(f => { counts[f.status] = (counts[f.status] || 0) + 1; });
  const max = Math.max(1, ...Object.values(counts));

  box.innerHTML = `
    <div class="panel-head" style="padding:0 0 10px;"><h3>Today's flying</h3></div>
    <div class="stat-value-row"><span class="stat-value">${todays.length}</span></div>
    <div class="stat-foot">departures scheduled today</div>
    <div class="mini-bars" style="padding-left:0;padding-right:0;margin-top:12px;">
      ${order.filter(s => counts[s]).map(s => `
        <div class="mini-bar-row">
          <span class="mini-bar-label">${s}</span>
          <span class="mini-bar-track">
            <span class="mini-bar-fill" style="width:${(counts[s] / max) * 100}%"></span>
          </span>
          <span class="mini-bar-value">${counts[s]}</span>
        </div>`).join("") || `<div class="cell-dim">No departures today.</div>`}
    </div>`;
}


// ---------- staff: what needs a human ----------
function renderOpsAttention(flights, bookings) {
  const box = document.getElementById("widgetAttention");
  const rows = [];

  const disrupted = flights.filter(f => f.status === "Delayed" || f.status === "Cancelled");
  const pendingPay = bookings.filter(b => b.paymentStatus === "Pending");
  const unpaidConfirmed = bookings.filter(b => b.status === "Confirmed" && !b.isPaid);
  const emptyBookings = bookings.filter(b => b.status !== "Cancelled" && b.totalAmount === 0);

  disrupted.slice(0, 3).forEach(f => rows.push({
    cls: f.status === "Cancelled" ? "attention-bad" : "attention-warn",
    icon: f.status === "Cancelled" ? "✕" : "!",
    title: `${f.flightNumber} ${f.status.toLowerCase()}`,
    sub: `${f.originCode}→${f.destinationCode} · ${formatDate(f.departureTime)}`,
    href: "flights.html", action: "View"
  }));

  if (pendingPay.length) rows.push({
    cls: "attention-warn", icon: "$",
    title: `${pendingPay.length} payment${pendingPay.length > 1 ? "s" : ""} awaiting confirmation`,
    sub: "Cash and bank transfers confirmed at the desk",
    href: "payments.html", action: "Review"
  });

  // a confirmed booking with no money against it is the one that actually
  // costs the airline - it's the same thing BookingsController's unpaidOnly
  // filter exists to surface
  if (unpaidConfirmed.length) rows.push({
    cls: "attention-bad", icon: "!",
    title: `${unpaidConfirmed.length} confirmed booking${unpaidConfirmed.length > 1 ? "s" : ""} unpaid`,
    sub: "Confirmed but no completed payment",
    href: "bookings.html", action: "Review"
  });

  if (emptyBookings.length) rows.push({
    cls: "attention-warn", icon: "○",
    title: `${emptyBookings.length} booking${emptyBookings.length > 1 ? "s" : ""} with no tickets`,
    sub: "Abandoned before a flight was added",
    href: "bookings.html", action: "Review"
  });

  paint(box, rows, "Nothing needs attention right now.");
}


function paint(box, rows, emptyMessage) {
  if (!box) return;
  box.innerHTML = rows.length
    ? rows.map(r => `
      <div class="attention-row">
        <span class="attention-icon ${r.cls}">${r.icon}</span>
        <span class="attention-body">
          <span class="attention-title">${escapeHtml(r.title)}</span><br>
          <span class="attention-sub">${escapeHtml(r.sub)}</span>
        </span>
        <a class="btn-outline" href="${r.href}">${escapeHtml(r.action)}</a>
      </div>`).join("")
    : `<div class="attention-row">
         <span class="attention-icon attention-ok">✓</span>
         <span class="attention-body"><span class="attention-title">${escapeHtml(emptyMessage)}</span></span>
       </div>`;
}

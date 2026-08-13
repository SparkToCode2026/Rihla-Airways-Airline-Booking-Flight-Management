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

async function loadDashboard() {
  try {
    // /flights is paginated - { total, page, pageSize, items } - not a bare
    // array like every other list endpoint. unwrap it here
    const [flightsPage, bookings, airplanes, users] = await Promise.all([
      api.get("/flights?pageSize=200").catch(() => ({ total: 0, items: [] })),
      api.get("/bookings").catch(() => []),
      api.get("/airplanes").catch(() => []),
      api.get("/users").catch(() => [])
    ]);
    const flights = flightsPage.items || [];

    document.getElementById("statFlights").textContent = flightsPage.total ?? flights.length;
    document.getElementById("statBookings").textContent = bookings.length;
    document.getElementById("statAirplanes").textContent = airplanes.length;
    document.getElementById("statUsers").textContent = users.length;

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

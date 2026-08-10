using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Text;
using RihlaAirways.Api.Data;

namespace RihlaAirways.Api.Services;

public class EmailService : IEmailService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(AppDbContext context, IConfiguration config,
                        ILogger<EmailService> logger)
    {
        _context = context;
        _config = config;
        _logger = logger;
    }


    // ================= trigger 1: booking confirmation =================
    public async Task SendBookingConfirmationAsync(int bookingId)
    {
        // one query pulling everything the e-ticket needs, five tables deep:
        // Booking -> User (who), Tickets -> Flight -> Route -> Airport (where),
        // Tickets -> SeatClass (what fare), Tickets -> Baggages (how much luggage).
        // this is the widest join in the project and it still translates
        // to a single SQL statement
        var booking = await _context.Bookings
            .Where(b => b.Id == bookingId)
            .Select(b => new
            {
                b.Id,
                b.BookingDate,
                b.TotalAmount,
                CustomerName = b.User.Name,
                CustomerEmail = b.User.Email,
                PaymentMethod = b.Payment != null ? b.Payment.Method : "—",
                Tickets = b.Tickets.Select(t => new
                {
                    t.PassengerName,
                    t.SeatNumber,
                    t.Price,
                    SeatClass = t.SeatClass.Name,
                    t.Flight.FlightNumber,
                    t.Flight.DepartureTime,
                    t.Flight.ArrivalTime,
                    Origin = t.Flight.Route.OriginAirport.Code,
                    OriginCity = t.Flight.Route.OriginAirport.City,
                    Destination = t.Flight.Route.DestinationAirport.Code,
                    DestinationCity = t.Flight.Route.DestinationAirport.City,
                    BagCount = t.Baggages.Count,
                    BagFees = t.Baggages.Sum(bg => bg.Fee)
                }).ToList()
            })
            .FirstOrDefaultAsync();

        // edge case first: the booking might have been deleted between the
        // payment completing and this running. log and return rather than
        // throw - an email failure must never break the payment
        if (booking == null)
        {
            _logger.LogWarning("Booking {Id} not found, skipping confirmation email.", bookingId);
            return;
        }

        if (booking.Tickets.Count == 0)
        {
            _logger.LogWarning("Booking {Id} has no tickets, skipping confirmation email.", bookingId);
            return;
        }

        var reference = $"RA-{booking.Id:D6}";

        var rows = string.Join("", booking.Tickets.Select(t => $@"
            <tr>
              <td style='padding:12px;border-bottom:1px solid #e5e7eb'>
                <strong>{t.FlightNumber}</strong><br>
                <span style='color:#6b7280;font-size:13px'>{t.Origin} → {t.Destination}</span>
              </td>
              <td style='padding:12px;border-bottom:1px solid #e5e7eb'>
                {t.DepartureTime:ddd d MMM, HH:mm}<br>
                <span style='color:#6b7280;font-size:13px'>arrives {t.ArrivalTime:HH:mm}</span>
              </td>
              <td style='padding:12px;border-bottom:1px solid #e5e7eb'>
                {t.PassengerName}<br>
                <span style='color:#6b7280;font-size:13px'>seat {t.SeatNumber} · {t.SeatClass}</span>
              </td>
              <td style='padding:12px;border-bottom:1px solid #e5e7eb;text-align:right'>
                {t.Price:F2} OMR
              </td>
            </tr>"));

        var html = $@"
<!DOCTYPE html>
<html><body style='margin:0;padding:0;background:#f3f4f6;font-family:-apple-system,Segoe UI,Roboto,sans-serif'>
  <div style='max-width:640px;margin:0 auto;background:#ffffff'>

    <div style='background:#0f3d3e;padding:24px 32px'>
      <h1 style='margin:0;color:#ffffff;font-size:20px;font-weight:600'>Rihla Airways</h1>
      <p style='margin:4px 0 0;color:#9fd3d0;font-size:13px'>Booking confirmed</p>
    </div>

    <div style='padding:32px'>
      <p style='margin:0 0 8px;font-size:15px'>Hello {booking.CustomerName},</p>
      <p style='margin:0 0 24px;color:#4b5563;font-size:14px;line-height:1.6'>
        Your payment has been received and your booking is confirmed.
        Your e-ticket details are below.
      </p>

      <div style='background:#f9fafb;border:1px solid #e5e7eb;padding:16px;margin-bottom:24px'>
        <span style='color:#6b7280;font-size:12px;text-transform:uppercase;letter-spacing:0.5px'>Booking reference</span><br>
        <span style='font-size:22px;font-weight:600;letter-spacing:2px'>{reference}</span>
      </div>

      <table style='width:100%;border-collapse:collapse;font-size:14px'>
        <thead>
          <tr style='background:#f9fafb'>
            <th style='padding:10px 12px;text-align:left;font-size:12px;color:#6b7280;text-transform:uppercase'>Flight</th>
            <th style='padding:10px 12px;text-align:left;font-size:12px;color:#6b7280;text-transform:uppercase'>Departs</th>
            <th style='padding:10px 12px;text-align:left;font-size:12px;color:#6b7280;text-transform:uppercase'>Passenger</th>
            <th style='padding:10px 12px;text-align:right;font-size:12px;color:#6b7280;text-transform:uppercase'>Fare</th>
          </tr>
        </thead>
        <tbody>{rows}</tbody>
      </table>

      <table style='width:100%;margin-top:24px;font-size:14px'>
        <tr>
          <td style='padding:6px 0;color:#4b5563'>Paid by</td>
          <td style='padding:6px 0;text-align:right'>{booking.PaymentMethod}</td>
        </tr>
        <tr>
          <td style='padding:6px 0;font-weight:600;font-size:16px;border-top:2px solid #0f3d3e'>Total paid</td>
          <td style='padding:6px 0;text-align:right;font-weight:600;font-size:16px;border-top:2px solid #0f3d3e'>{booking.TotalAmount:F2} OMR</td>
        </tr>
      </table>

      <p style='margin:28px 0 0;color:#6b7280;font-size:13px;line-height:1.6'>
        Please arrive at the airport at least 2 hours before departure for
        international flights. Bring the passport used at booking.
      </p>
    </div>

    <div style='background:#f9fafb;padding:20px 32px;border-top:1px solid #e5e7eb'>
      <p style='margin:0;color:#9ca3af;font-size:12px'>
        This is an automated message from a student project. Please do not reply.
      </p>
    </div>

  </div>
</body></html>";

        // plain text alternative in the same message. mail clients that
        // can't render html fall back to this, and including it is what
        // stops a message being scored as spam for being html-only
        var text = $@"RIHLA AIRWAYS — BOOKING CONFIRMED

Hello {booking.CustomerName},

Your payment has been received and your booking is confirmed.

Booking reference: {reference}

{string.Join("\n\n", booking.Tickets.Select(t =>
    $"  {t.FlightNumber}  {t.Origin} -> {t.Destination}\n" +
    $"  Departs {t.DepartureTime:ddd d MMM yyyy, HH:mm} · arrives {t.ArrivalTime:HH:mm}\n" +
    $"  {t.PassengerName} · seat {t.SeatNumber} · {t.SeatClass}\n" +
    $"  Fare: {t.Price:F2} OMR"))}

Paid by:    {booking.PaymentMethod}
Total paid: {booking.TotalAmount:F2} OMR

Please arrive at least 2 hours before departure for international flights.

This is an automated message from a student project. Please do not reply.";

        await SendAsync(booking.CustomerEmail, booking.CustomerName,
            $"Booking confirmed — {reference}", html, text);
    }


    // ================= trigger 2: flight status =================
    public async Task SendFlightStatusAsync(int flightId, string newStatus)
    {
        var flight = await _context.Flights
            .Where(f => f.Id == flightId)
            .Select(f => new
            {
                f.FlightNumber,
                f.DepartureTime,
                f.ArrivalTime,
                Origin = f.Route.OriginAirport.Code,
                OriginCity = f.Route.OriginAirport.City,
                Destination = f.Route.DestinationAirport.Code,
                DestinationCity = f.Route.DestinationAirport.City
            })
            .FirstOrDefaultAsync();

        if (flight == null)
        {
            _logger.LogWarning("Flight {Id} not found, skipping status email.", flightId);
            return;
        }

        // this is the fan-out: one status change, one email per affected
        // customer. the chain is Flight -> Tickets -> Booking -> User.
        // Distinct() matters - a family booking has several tickets on the
        // same flight under one user, and nobody wants four copies
        var recipients = await _context.Tickets
            .Where(t => t.FlightId == flightId && t.Booking.Status != "Cancelled")
            .Select(t => new
            {
                t.Booking.User.Name,
                t.Booking.User.Email,
                BookingId = t.BookingId
            })
            .Distinct()
            .ToListAsync();

        if (recipients.Count == 0)
        {
            _logger.LogInformation("Flight {Number} has no ticketed passengers, no emails sent.",
                flight.FlightNumber);
            return;
        }

        var (headline, message, accent) = newStatus switch
        {
            "Cancelled" => ("Flight cancelled",
                "We're sorry — this flight has been cancelled. Our team will contact you " +
                "about rebooking or a refund.", "#b91c1c"),
            "Delayed"   => ("Flight delayed",
                "This flight has been delayed. Please check back for an updated departure " +
                "time before travelling to the airport.", "#b45309"),
            "Boarding"  => ("Now boarding",
                "Your flight is now boarding. Please proceed to the gate.", "#0f3d3e"),
            _           => ($"Flight status: {newStatus}",
                $"The status of your flight has changed to {newStatus}.", "#0f3d3e")
        };

        foreach (var r in recipients)
        {
            var reference = $"RA-{r.BookingId:D6}";

            var html = $@"
<!DOCTYPE html>
<html><body style='margin:0;padding:0;background:#f3f4f6;font-family:-apple-system,Segoe UI,Roboto,sans-serif'>
  <div style='max-width:640px;margin:0 auto;background:#ffffff'>

    <div style='background:{accent};padding:24px 32px'>
      <h1 style='margin:0;color:#ffffff;font-size:20px;font-weight:600'>Rihla Airways</h1>
      <p style='margin:4px 0 0;color:#ffffff;opacity:0.85;font-size:13px'>{headline}</p>
    </div>

    <div style='padding:32px'>
      <p style='margin:0 0 8px;font-size:15px'>Hello {r.Name},</p>
      <p style='margin:0 0 24px;color:#4b5563;font-size:14px;line-height:1.6'>{message}</p>

      <div style='background:#f9fafb;border:1px solid #e5e7eb;padding:20px'>
        <div style='font-size:22px;font-weight:600;margin-bottom:6px'>{flight.FlightNumber}</div>
        <div style='color:#4b5563;font-size:14px'>
          {flight.OriginCity} ({flight.Origin}) → {flight.DestinationCity} ({flight.Destination})
        </div>
        <div style='color:#6b7280;font-size:13px;margin-top:10px'>
          Scheduled departure: {flight.DepartureTime:ddd d MMM yyyy, HH:mm}
        </div>
        <div style='margin-top:14px;padding-top:14px;border-top:1px solid #e5e7eb;font-size:13px;color:#6b7280'>
          Booking reference <strong style='color:#111827'>{reference}</strong>
        </div>
      </div>
    </div>

    <div style='background:#f9fafb;padding:20px 32px;border-top:1px solid #e5e7eb'>
      <p style='margin:0;color:#9ca3af;font-size:12px'>
        This is an automated message from a student project. Please do not reply.
      </p>
    </div>

  </div>
</body></html>";

            var text = $@"RIHLA AIRWAYS — {headline.ToUpper()}

Hello {r.Name},

{message}

  Flight:    {flight.FlightNumber}
  Route:     {flight.OriginCity} ({flight.Origin}) -> {flight.DestinationCity} ({flight.Destination})
  Scheduled: {flight.DepartureTime:ddd d MMM yyyy, HH:mm}
  Booking:   {reference}

This is an automated message from a student project. Please do not reply.";

            await SendAsync(r.Email, r.Name,
                $"{headline} — {flight.FlightNumber}", html, text);
            
            await Task.Delay(600);
        }

        // "Attempted", not "Sent" - this counts recipients, not successful
        // sends. SendAsync swallows its own failures, so the old wording
        // reported "Sent 2" when only one actually landed
        _logger.LogInformation("Attempted {Count} status email(s) for flight {Number} ({Status}).",
            recipients.Count, flight.FlightNumber, newStatus);
    }


    // ================= the actual SMTP send =================
    private async Task SendAsync(string toEmail, string toName,
                                 string subject, string html, string text)
    {
        // kill switch. a teammate cloning the repo without mailtrap
        // credentials would otherwise get a connection exception on every
        // payment - this lets them run the project with Enabled = false
        if (!_config.GetValue("Email:Enabled", false))
        {
            _logger.LogInformation("Email disabled — would have sent '{Subject}' to {To}.",
                subject, toEmail);
            return;
        }

        // read the config ONCE and validate up front. the compiler warns on
        // _config["..."] because it returns string? - a missing key gives
        // null, and passing null into MailKit fails somewhere deep inside
        // the library with a message that doesn't mention config at all.
        // failing here says exactly which key is missing - new fix
        var host = _config["Email:Host"];
        var username = _config["Email:Username"];
        var password = _config["Email:Password"];
        var fromAddress = _config["Email:FromAddress"];
        var fromName = _config["Email:FromName"] ?? "Rihla Airways";
        var port = _config.GetValue<int>("Email:Port");

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fromAddress))
        {
            _logger.LogError("Email config incomplete — check Email:Host, Username, " +
                             "Password and FromAddress in appsettings. Nothing sent.");
            return;
        }

        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(fromName, fromAddress));
            msg.To.Add(new MailboxAddress(toName, toEmail));
            msg.Subject = subject;

            // multipart/alternative - both bodies in ONE message. the client
            // picks whichever it can render. this is why we bothered writing
            // the plain text version
            var body = new BodyBuilder
            {
                HtmlBody = html,
                TextBody = text
            };
            msg.Body = body.ToMessageBody();

            using var client = new SmtpClient();

            // StartTls, not SslOnConnect. mailtrap's port 2525 starts as a
            // plain connection and upgrades - forcing SslOnConnect here just
            // hangs until it times out, which is a confusing failure to debug
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(username, password);

            await client.SendAsync(msg);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Sent '{Subject}' to {To}.", subject, toEmail);
        }
        
        catch (Exception ex)
        {
            // swallowed on purpose, and this is the important design decision
            // in the file. this method gets called from inside
            // PaymentsController right after money is recorded as received.
            // if we let the exception escape, a mail server being down would
            // fail the payment request - the customer's money is taken and
            // the transaction reports an error. logging and moving on is the
            // right trade at this scope.
            // the production answer is a background queue: write a "send this"
            // row, return immediately, let a worker retry. out of scope here,
            // but worth saying out loud in the demo
            _logger.LogError(ex, "Failed to send '{Subject}' to {To}.", subject, toEmail);
        }
    }
}
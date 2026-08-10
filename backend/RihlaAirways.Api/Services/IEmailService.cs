namespace RihlaAirways.Api.Services;

// an interface, unlike TokenService which is a plain class. two reasons:
// the controllers depend on the contract instead of the SMTP implementation,
// and it means a teammate can swap in a fake for testing without touching
// PaymentsController at all
public interface IEmailService
{
    // the spec's first trigger - fires when a payment completes
    Task SendBookingConfirmationAsync(int bookingId);

    // the spec's second trigger - fires when a flight is delayed or cancelled.
    // one call, many recipients: every passenger holding a ticket
    Task SendFlightStatusAsync(int flightId, string newStatus);
}
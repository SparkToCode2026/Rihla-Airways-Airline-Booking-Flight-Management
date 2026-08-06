//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;

namespace RihlaAirways.Api.Models;
public class Payment
{
    public int Id { get; set; }

    // one-to-one, FK on the dependent side (total)
    public int BookingId { get; set; }
    public decimal Amount { get; set; }

    public DateTime PaymentDate { get; set; } = DateTime.UtcNow;
    
    // Card / Cash / BankTransfer
    [Required]
    [MaxLength(30)]
    public string Method { get; set; } = string.Empty;
    
    // Pending / Completed / Failed / Refunded.
    // this is our email trigger - when Status flips to "Completed"
    // the confirmation email fires. w
    [Required] [MaxLength(20)] public string Status { get; set; } = "Pending";
    
    // --- navigation ---
    public Booking Booking { get; set; } = null!;

}
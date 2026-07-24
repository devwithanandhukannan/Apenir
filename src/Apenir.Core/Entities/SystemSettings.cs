using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities;

[Table("system_settings")]
public class SystemSettings
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // WhatsApp Settings
    public string? WhatsAppAccessToken { get; set; }
    public string? WhatsAppPhoneNumberId { get; set; }
    public string? WhatsAppApiVersion { get; set; }

    // Razorpay Settings
    public string? RazorpayKeyId { get; set; }
    public string? RazorpayKeySecret { get; set; }
    public string? RazorpayWebhookSecret { get; set; }

    // SMTP Settings
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool SmtpEnableSsl { get; set; }
    public string? SmtpFromEmail { get; set; }
}

using System.Threading.Tasks;
using Apenir.Core.Entities;
using Apenir.Application.Common.Models;

namespace Apenir.Application.Common.Interfaces;

public interface ISettingsService
{
    Task<SystemSettings> GetSettingsAsync();
    Task SaveSettingsAsync(SystemSettings settings);
    Task<string?> GetWhatsAppAccessTokenAsync();
    Task<string?> GetWhatsAppPhoneNumberIdAsync();
    Task<string?> GetWhatsAppApiVersionAsync();
    Task<string?> GetRazorpayKeyIdAsync();
    Task<string?> GetRazorpayKeySecretAsync();
    Task<string?> GetRazorpayWebhookSecretAsync();
    Task<SmtpSettings> GetSmtpSettingsAsync();
}

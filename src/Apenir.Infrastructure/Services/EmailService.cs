using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;

namespace Apenir.Infrastructure.Services
{
    public class EmailService : IEmailService
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger<EmailService> _logger;

        public EmailService(ISettingsService settingsService, ILogger<EmailService> logger)
        {
            _settingsService = settingsService;
            _logger = logger;
        }

        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            var smtpSettings = await _settingsService.GetSmtpSettingsAsync();

            if (string.IsNullOrWhiteSpace(smtpSettings.Host) || string.IsNullOrWhiteSpace(smtpSettings.Username))
            {
                _logger.LogWarning("[MOCK EMAIL SENDER - SmtpSettings NOT CONFIGURED]");
                _logger.LogWarning("To: {ToEmail}", toEmail);
                _logger.LogWarning("Subject: {Subject}", subject);
                _logger.LogWarning("Body: {Body}", body);
                return;
            }

            try
            {
                using (var client = new SmtpClient(smtpSettings.Host, smtpSettings.Port))
                {
                    client.UseDefaultCredentials = false;
                    client.Credentials = new NetworkCredential(smtpSettings.Username, smtpSettings.Password);
                    client.EnableSsl = smtpSettings.EnableSsl;

                    var mailMessage = new MailMessage
                    {
                        From = new MailAddress(smtpSettings.FromEmail),
                        Subject = subject,
                        Body = body,
                        IsBodyHtml = true
                    };

                    mailMessage.To.Add(toEmail);

                    await client.SendMailAsync(mailMessage);
                    _logger.LogInformation("Email successfully sent to {ToEmail}", toEmail);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {ToEmail}", toEmail);
                throw;
            }
        }
    }
}

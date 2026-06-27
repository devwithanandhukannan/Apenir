namespace Apenir.Core.Interfaces;

public interface IWhatsAppService
{
    Task SendTextMessageAsync(string toPhone, string message);
}
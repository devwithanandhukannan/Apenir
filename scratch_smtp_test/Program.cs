using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        string host = "smtp.gmail.com";
        int port = 587;
        string username = "abinjos307@gmail.com";
        string password = "filu klvb amly eict";
        string from = "abinjos307@gmail.com";
        string to = "abinjos307@gmail.com";

        Console.WriteLine($"SMTP Connection Test: Host={host}, Port={port}, User={username}");

        try
        {
            using (var client = new SmtpClient(host, port))
            {
                client.UseDefaultCredentials = false;
                client.Credentials = new NetworkCredential(username, password);
                client.EnableSsl = true;

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(from),
                    Subject = "Apenir SMTP Verification Test",
                    Body = "<h1>Apenir SMTP connection verified successfully.</h1>",
                    IsBodyHtml = true
                };

                mailMessage.To.Add(to);

                Console.WriteLine("Sending test email...");
                await client.SendMailAsync(mailMessage);
                Console.WriteLine("SUCCESS: Email sent successfully!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: Failed to send email!");
            Console.WriteLine(ex.ToString());
        }
    }
}

namespace re.colocar.me.talent.Domain.Authenticate;

public record LinkedinProfile(string Sub, bool Email_verified, string Name, Locale Locale, string Given_name, string Family_name, string Email, string Picture)
{
    public string? AccessToken { get; set; }
    public Guid UserId { get; set; }
}

public class Locale
{
    public string? Country { get; set; }
    public string? Language { get; set; }
}


namespace re.colocar.me.talent.Domain;
public class FacebookAuthRequest
{
    public string? UserID { get; set; }
    public int ExpiresIn { get; set; }

    public string? AccessToken { get; set; }

    public string? Status { get; set; }

}
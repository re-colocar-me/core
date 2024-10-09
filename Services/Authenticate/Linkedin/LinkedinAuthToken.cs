using Newtonsoft.Json;
namespace re.colocar.me.talent.Domain.Authenticate;

public class LinkedinAuthToken
{
    [JsonProperty("access_token")]
    public string? AccessToken { get; set; }

    [JsonProperty("expires_in")]
    public int? ExpiresIn { get; set; }

    public string? Scope { get; set; }

}
namespace re.colocar.me.talent.Domain.Authenticate.Facebook
{
    public class FacebookProfile
    {
        public string? Id { get; set; }
        public string Name { get; set; }        
        public string Email { get; set; }
        public Picture? Picture { get; set; }
        public string AccessToken { get; set; }
    }
}

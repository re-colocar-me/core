namespace re.colocar.me.talent.Domain.Authenticate.Facebook
{
    public class Picture
    {
        public PictureData Data { get; set; }
    }

    public class PictureData
    {
        public int Height { get; set; }        
        public string Url { get; set; }
        public int Width { get; set; }
    }
}


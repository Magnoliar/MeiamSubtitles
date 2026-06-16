namespace Emby.MeiamSub.Zimuku.Model
{
    /// <summary>
    /// AI API 响应模型
    /// </summary>
    public class AIResponse
    {
        public AIChoice[] Choices { get; set; }
    }

    public class AIChoice
    {
        public AIMessage Message { get; set; }
    }

    public class AIMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }
}

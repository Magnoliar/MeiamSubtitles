using System;
using System.Collections.Generic;
using System.Text;

namespace Emby.MeiamSub.SubHD.Model
{
    public class DownloadSubInfo
    {
        public string SubId { get; set; }
        public string Title { get; set; }
        public string Format { get; set; }
        public string Language { get; set; }
        public string TwoLetterISOLanguageName { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.MeiamSub.Zimuku.Model
{
    public class DownloadSubInfo
    {
        public string DetailUrl { get; set; }
        public string Language { get; set; }
        public string TwoLetterISOLanguageName { get; set; }
        public string Format { get; set; }
        public string Name { get; set; }
    }
}

using System;

namespace SarahsToolkit.Models
{
    public class GameRelease
    {
        public string Title { get; set; } = "";
        public DateTime Date { get; set; }
        public string Platforms { get; set; } = "";
        public string Genre { get; set; } = "";
    }
}

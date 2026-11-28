using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace VoxStride.Desktop;

public enum HairStyle { Bangs, SideSwept, Long, LongWithBun, Spiky, Crop }

/// <summary>
/// One of VoxStride's own characters (all original designs): look, default Gemini voice and personality. The user's own
/// personality text, if set, replaces the character's.
/// </summary>
public sealed record CompanionCharacter(
    string Id, string Name, string Tagline, string Voice, string Personality,
    Color Skin, Color SkinShade, Color HairTop, Color HairBottom, Color HoodieA, Color HoodieB, Color Iris,
    HairStyle Hair, bool Glasses = false, bool Beard = false, bool Freckles = false)
{
    public override string ToString() => $"{Name} — {Tagline}";
}

public static class CompanionCharacters
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly IReadOnlyList<CompanionCharacter> All =
    [
        new("nova", "Nova", "warm and playful", "Sulafat",
            "warm, upbeat and a little playful; loves chatting and cheering the user on",
            C("#FFFFE1CC"), C("#FFF5C9AE"), C("#FF3B2F6B"), C("#FF221B45"), C("#FF7C5CFF"), C("#FF4F7BFF"), C("#FF5A3FD0"), HairStyle.Bangs),
        new("ayaan", "Ayaan", "calm and clever", "Achird",
            "calm, clever and patient; explains things simply and likes to plan things step by step",
            C("#FFE8B892"), C("#FFD9A27C"), C("#FF1E1B1A"), C("#FF0E0C0B"), C("#FF16A394"), C("#FF1E7FB8"), C("#FF4A3424"), HairStyle.SideSwept, Glasses: true),
        new("zara", "Zara", "cheerful and chatty", "Aoede",
            "cheerful, chatty and curious; enjoys small talk, jokes and asking how the user's day is going",
            C("#FFF7D2BA"), C("#FFEBB898"), C("#FF6B3A1F"), C("#FF4A2412"), C("#FFFF7A7A"), C("#FFFF5C9A"), C("#FF3E7A3A"), HairStyle.LongWithBun, Freckles: true),
        new("kai", "Kai", "energetic gamer buddy", "Puck",
            "energetic, fun and competitive; a gaming buddy who gets excited, gives quick tips and keeps things light",
            C("#FFFFDCC2"), C("#FFF2C2A0"), C("#FFFF8A2A"), C("#FFE0561A"), C("#FF34C46A"), C("#FF17A2B8"), C("#FF2F6FD0"), HairStyle.Spiky),
        new("sage", "Sage", "gentle and thoughtful", "Vindemiatrix",
            "gentle, thoughtful and kind; speaks softly, listens well and helps the user think things through",
            C("#FFFFE6D6"), C("#FFF3CDB8"), C("#FFD9DCE6"), C("#FFA8AEC2"), C("#FF34457A"), C("#FF5B6FB8"), C("#FF6A4CB0"), HairStyle.Long),
        new("rehan", "Rehan", "focused professional", "Charon",
            "focused, professional and to the point; gives clear, short answers and gets work done quickly",
            C("#FFD9A27C"), C("#FFC68B66"), C("#FF2A2420"), C("#FF16120F"), C("#FF8E2A3C"), C("#FF5E1E3A"), C("#FF3B2A1E"), HairStyle.Crop, Beard: true),
    ];

    public static CompanionCharacter Get(string? id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];
}

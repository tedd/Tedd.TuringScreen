namespace Tedd.TuringScreen;

public enum Color656 : UInt16
{
    /*

       public static UInt16 RGBTo565(byte r, byte g, byte b)
       {
       	int r5 = (r * 31 + 127) / 255;   // round to nearest
       	int g6 = (g * 63 + 127) / 255;
       	int b5 = (b * 31 + 127) / 255;
       
       	return (ushort)((r5 << 11) | (g6 << 5) | b5);
       }

	var colorProps =
       	typeof(Color)
       		.GetProperties(BindingFlags.Public | BindingFlags.Static)
       		.Where(p => p.PropertyType == typeof(Color));

       foreach (var prop in colorProps.OrderBy(p => p.Name))
       {
       	var c = (Color)prop.GetValue(null)!;

       	// Optional: skip non-opaque (e.g. Transparent)
       	if (c.A != 255)
       		continue;

       	UInt16 value = RGBTo565(c.R, c.G, c.B);

       	// Decimal
       	//$"{prop.Name} = {value},".Dump();

       	// Hex
       	$"{prop.Name} = 0x{value:X4},".Dump();
       }
     */
    AliceBlue = 0xEFBF,
    AntiqueWhite = 0xF75A,
    Aqua = 0x07FF,
    Aquamarine = 0x7FFA,
    Azure = 0xEFFF,
    Beige = 0xF7BB,
    Bisque = 0xFF18,
    Black = 0x0000,
    BlanchedAlmond = 0xFF59,
    Blue = 0x001F,
    BlueViolet = 0x897B,
    Brown = 0xA145,
    BurlyWood = 0xDDB0,
    CadetBlue = 0x64F3,
    Chartreuse = 0x7FE0,
    Chocolate = 0xD344,
    Coral = 0xFBEA,
    CornflowerBlue = 0x64BD,
    Cornsilk = 0xFFBB,
    Crimson = 0xD8A7,
    Cyan = 0x07FF,
    DarkBlue = 0x0011,
    DarkCyan = 0x0451,
    DarkGoldenrod = 0xB421,
    DarkGray = 0xAD55,
    DarkGreen = 0x0320,
    DarkKhaki = 0xBDAD,
    DarkMagenta = 0x8811,
    DarkOliveGreen = 0x5346,
    DarkOrange = 0xFC60,
    DarkOrchid = 0x9999,
    DarkRed = 0x8800,
    DarkSalmon = 0xE4AF,
    DarkSeaGreen = 0x8DD1,
    DarkSlateBlue = 0x49F1,
    DarkSlateGray = 0x328A,
    DarkTurquoise = 0x0679,
    DarkViolet = 0x901A,
    DeepPink = 0xF8B2,
    DeepSkyBlue = 0x05FF,
    DimGray = 0x6B4D,
    DodgerBlue = 0x249F,
    Firebrick = 0xB104,
    FloralWhite = 0xFFDD,
    ForestGreen = 0x2444,
    Fuchsia = 0xF81F,
    Gainsboro = 0xDEDB,
    GhostWhite = 0xF7BF,
    Gold = 0xFEA0,
    Goldenrod = 0xDD24,
    Gray = 0x8410,
    Green = 0x0400,
    GreenYellow = 0xAFE6,
    Honeydew = 0xEFFD,
    HotPink = 0xFB56,
    IndianRed = 0xCAEB,
    Indigo = 0x4810,
    Ivory = 0xFFFD,
    Khaki = 0xEF31,
    Lavender = 0xE73E,
    LavenderBlush = 0xFF7E,
    LawnGreen = 0x7FC0,
    LemonChiffon = 0xFFD9,
    LightBlue = 0xAEBC,
    LightCoral = 0xEC10,
    LightCyan = 0xDFFF,
    LightGoldenrodYellow = 0xF7DA,
    LightGray = 0xD69A,
    LightGreen = 0x9772,
    LightPink = 0xFDB7,
    LightSalmon = 0xFD0F,
    LightSeaGreen = 0x2595,
    LightSkyBlue = 0x867E,
    LightSlateGray = 0x7453,
    LightSteelBlue = 0xAE1B,
    LightYellow = 0xFFFB,
    Lime = 0x07E0,
    LimeGreen = 0x3666,
    Linen = 0xF77C,
    Magenta = 0xF81F,
    Maroon = 0x8000,
    MediumAquamarine = 0x6675,
    MediumBlue = 0x0019,
    MediumOrchid = 0xBABA,
    MediumPurple = 0x939B,
    MediumSeaGreen = 0x3D8E,
    MediumSlateBlue = 0x7B5D,
    MediumSpringGreen = 0x07D3,
    MediumTurquoise = 0x4E99,
    MediumVioletRed = 0xC0B0,
    MidnightBlue = 0x18CE,
    MintCream = 0xF7FE,
    MistyRose = 0xFF1B,
    Moccasin = 0xFF16,
    NavajoWhite = 0xFEF5,
    Navy = 0x0010,
    OldLace = 0xFFBC,
    Olive = 0x8400,
    OliveDrab = 0x6C64,
    Orange = 0xFD20,
    OrangeRed = 0xFA20,
    Orchid = 0xDB9A,
    PaleGoldenrod = 0xEF35,
    PaleGreen = 0x97D2,
    PaleTurquoise = 0xAF7D,
    PaleVioletRed = 0xDB92,
    PapayaWhip = 0xFF7A,
    PeachPuff = 0xFED6,
    Peru = 0xCC28,
    Pink = 0xFDF9,
    Plum = 0xDD1B,
    PowderBlue = 0xAEFC,
    Purple = 0x8010,
    RebeccaPurple = 0x61B3,
    Red = 0xF800,
    RosyBrown = 0xBC71,
    RoyalBlue = 0x435B,
    SaddleBrown = 0x8A22,
    Salmon = 0xF40E,
    SandyBrown = 0xF52C,
    SeaGreen = 0x344B,
    SeaShell = 0xFFBD,
    Sienna = 0x9A85,
    Silver = 0xBDF7,
    SkyBlue = 0x867D,
    SlateBlue = 0x6AD9,
    SlateGray = 0x7412,
    Snow = 0xFFDE,
    SpringGreen = 0x07EF,
    SteelBlue = 0x4C16,
    Tan = 0xD591,
    Teal = 0x0410,
    Thistle = 0xD5FA,
    Tomato = 0xFB09,
    Turquoise = 0x46F9,
    Violet = 0xEC1D,
    Wheat = 0xF6F6,
    White = 0xFFFF,
    WhiteSmoke = 0xF7BE,
    Yellow = 0xFFE0,
    YellowGreen = 0x9E66,
}
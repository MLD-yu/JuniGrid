using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 用游戏本体的贴图合成存档角色的站立小像（正面、20×34 画布含帽子外扩，输出 3 倍最近邻）。
/// <para>逐层规则对照反编译 <c>StardewValley.FarmerRenderer.draw / drawHairAndAccesories</c>（1.6.15）：</para>
/// <list type="number">
/// <item>身体底图是调色板贴图：皮肤 260–262 / 鞋 268–271 / 眼 276–277 / 袖 256–258 按锚点整色替换。
///       袖子有袖时取衬衫贴图 (shx,shy+4-k) 与染色半区同位像素相乘（染色像素半透明则退回本色×白），
///       无袖（Data/Shirts HasSleeves=false）换皮肤三色。</item>
/// <item>裤子：pantsItem.indexInTileSheet，块 (i%10*192+女96, i/10*688) 同格叠乘 pantsColor。</item>
/// <item>衬衫：shirtItem.indexInTileSheet，8×8 贴 (4,15)，本色层+染色半区(x+128)×shirtColor 两层都画。</item>
/// <item>配件：accessories 表 16×16，正面贴 (0,2)；胡须类(&lt;6 或 19–22)乘发色，其余本色；
///       &lt;8 或胡须类画在头发下，其余画在头发上。</item>
/// <item>头发：0–31 走 hairstyles（每格 16×96 取正面段）；100+ 走 Data/HairData 元数据贴图/坐标；
///       32–55 是 1.6 无效号——游戏源矩形越界夹到透明边，等于不画。戴帽按 hairDrawType 隐藏/换受遮发型。</item>
/// <item>帽子：hats 表 20×80 取正面段，贴 (-2,-2+hairstyleHatOffset[hair%16]/4)。</item>
/// <item>手臂：底图第 6 列（armOffset=6）单独一层，画在头发/帽子之上——站立帧的手臂不在第 0 列里。</item>
/// </list>
/// <para>朝向编号 0=上 2=下；frame 0 的 featureYOffset=1，头发/衬衫/配件的纵向偏移都含它。</para>
///
/// <para><b>贴图来源</b>：本体 <c>Content\Characters\Farmer\*.xnb</c>，但先经
/// <see cref="FarmerAppearancePacks"/> 叠一层「已启用 Content Patcher 包的换图」——
/// 不然游戏里换了优雅农夫身体、贴身发型，列表里还是原版那个人，用户会当成 bug。</para>
/// </summary>
public static class FarmerAvatarService
{
    private const int CellW = 20;
    private const int CellH = 34;
    private const int Ox = 2;
    private const int Oy = 2;
    // 3 倍 = 60×102。CSS 显示 40×68，Windows 150% 缩放下正好 1 设备像素 = 1 图像像素。
    private const int Scale = 3;

    private static readonly int[] HatOffset = { 0, 0, 0, 4, 0, 0, 3, 0, 4, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly (int R, int G, int B) Prismatic = (255, 0, 0); // 静态小像取棱彩循环起点

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DecodedTexture> Textures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> Avatars = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, bool>> SleeveTables = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<int, HairMeta>> HairTables = new(StringComparer.Ordinal);

    private sealed record HairMeta(string Tex, int TileX, int TileY, int Covered);

    /// <summary>合成一张 data:image/png;base64。缺素材 / 解不出时返回 null，界面画占位。</summary>
    public static string? DataUri(SaveLook look, string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        var content = Path.Combine(gamePath, "Content", "Characters", "Farmer");
        var data = Path.Combine(gamePath, "Content", "Data");
        var key = string.Join('|', look.Male, look.Skin, look.Hair, look.Shoes, look.ShirtIndex, look.ShirtId,
            look.ShirtColor, look.ShirtPrismatic, look.PantsIndex, look.PantsColor, look.PantsPrismatic,
            look.HairColor, look.HairPrismatic, look.EyeColor, look.Accessory, look.HatIndex,
            look.HatHairDrawType, look.HatIgnoreHairOffset, look.HatPrismatic, Stamp(content), Stamp(data),
            FarmerAppearancePacks.Signature(gamePath));
        lock (Gate)
        {
            if (Avatars.TryGetValue(key, out var cached)) return cached;
        }

        string? uri = null;
        try { uri = Compose(look, gamePath, data); }
        catch (Exception ex) { AppLog.Warn("存档", "合成角色小像失败：" + ex.Message); }

        lock (Gate) Avatars[key] = uri;
        return uri;
    }

    /// <summary>素材目录的「版本指纹」：游戏升级换贴图后旧缓存自动失效。</summary>
    private static string Stamp(string dir)
    {
        try
        {
            var newest = 0L;
            foreach (var f in Directory.EnumerateFiles(dir, "*.xnb"))
                newest = Math.Max(newest, File.GetLastWriteTimeUtc(f).Ticks);
            return newest.ToString();
        }
        catch { return "0"; }
    }

    private static string FileStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks + ":" + new FileInfo(path).Length; }
        catch { return ""; }
    }

    /// <summary>
    /// 解一张本体贴图，**先叠已启用 Content Patcher 包的换图**再返回。
    /// 键里带补丁集指纹：装了/禁了角色外观 mod，或在 GMCM 里改了它的配置，缓存自动失效。
    /// </summary>
    private static DecodedTexture Load(string gamePath, string name, bool male)
    {
        var content = Path.Combine(gamePath, "Content", "Characters", "Farmer");
        var path = Path.Combine(content, name + ".xnb");
        var key = path + "|" + FileStamp(path) + "|" + (male ? "m" : "f")
                  + "|" + FarmerAppearancePacks.Signature(gamePath);
        if (Textures.TryGetValue(key, out var t)) return t;
        var baseTex = XnbDecoder.TryDecode(path) ?? throw new InvalidDataException("解不出 " + name);
        var asset = name.Contains('/') || name.Contains('\\')
            ? name.Replace('\\', '/')
            : "Characters/Farmer/" + name;
        var tex = FarmerAppearancePacks.Apply(gamePath, asset, baseTex, male);
        Textures[key] = tex;
        return tex;
    }

    /// <summary>
    /// 没叠换图的原始贴图 —— 只给「调色板锚点」用：mod 改过身体像素后，
    /// 按打完补丁的图去比对颜色会找不到锚点，而游戏是按固定位置换色的。
    /// </summary>
    private static DecodedTexture LoadBase(string gamePath, string name)
    {
        var path = Path.Combine(gamePath, "Content", "Characters", "Farmer", name + ".xnb");
        var key = path + "|" + FileStamp(path) + "|raw";
        if (Textures.TryGetValue(key, out var t)) return t;
        var tex = XnbDecoder.TryDecode(path) ?? throw new InvalidDataException("解不出 " + name);
        Textures[key] = tex;
        return tex;
    }

    private static string? Compose(SaveLook look, string gamePath, string data)
    {
        var baseName = look.Male ? "farmer_base" : "farmer_girl_base";
        var sheet = Load(gamePath, baseName, look.Male);
        var pants = Load(gamePath, "pants", look.Male);
        var shirts = Load(gamePath, "shirts", look.Male);
        var accessories = Load(gamePath, "accessories", look.Male);
        var hats = Load(gamePath, "hats", look.Male);
        var skinPal = Load(gamePath, "skinColors", look.Male);
        var shoePal = Load(gamePath, "shoeColors", look.Male);

        var px = (byte[])sheet.PixelsRgba.Clone();
        var W = sheet.Width;

        // 锚点索引按【原始】底图生成（同 FarmerRenderer.recolorOffsets），避免换色串锚。
        // 原始 = 没叠 mod 换图的那份：mod 身体重画过调色板像素时，按打完补丁的图去比对颜色
        // 会一个锚点都找不到（整张脸不换色），而游戏是按固定位置换色的 —— 这里对齐游戏。
        var anchorTex = LoadBase(gamePath, baseName);
        var anchorPx = anchorTex.Width == W ? anchorTex.PixelsRgba : sheet.PixelsRgba;
        var anchors = new Dictionary<int, List<int>>();
        foreach (var a in new[] { 256, 257, 258, 260, 261, 262, 268, 269, 270, 271, 276, 277 })
        {
            var ai = a * 4;
            var list = new List<int>();
            if (ai + 3 < anchorPx.Length && anchorPx[ai + 3] != 0)
                // 上界取两张图长度的较小者：mod 换的身体图与原版同宽不同高时，
                // 用打完补丁的长度去索引原版会越界（小像直接变空图）。
                for (var i = 0; i + 3 < px.Length && i + 3 < anchorPx.Length; i += 4)
                    if (anchorPx[i] == anchorPx[ai] && anchorPx[i + 1] == anchorPx[ai + 1]
                        && anchorPx[i + 2] == anchorPx[ai + 2] && anchorPx[i + 3] == anchorPx[ai + 3])
                        list.Add(i);
            anchors[a] = list;
        }

        void Swap(int anchor, (int R, int G, int B, int A) to)
        {
            foreach (var i in anchors[anchor])
            {
                px[i] = (byte)to.R; px[i + 1] = (byte)to.G; px[i + 2] = (byte)to.B; px[i + 3] = (byte)to.A;
            }
        }

        (int R, int G, int B, int A) Pal(DecodedTexture pal, int index, int row)
        {
            var y = Math.Clamp(row, 0, pal.Height - 1);
            var x = Math.Clamp(index, 0, pal.Width - 1);
            var i = (y * pal.Width + x) * 4;
            return (pal.PixelsRgba[i], pal.PixelsRgba[i + 1], pal.PixelsRgba[i + 2], pal.PixelsRgba[i + 3]);
        }

        (int R, int G, int B, int A) Pix(DecodedTexture tex, int x, int y)
        {
            if (x < 0 || y < 0 || x >= tex.Width || y >= tex.Height) return (0, 0, 0, 0);
            var i = (y * tex.Width + x) * 4;
            return (tex.PixelsRgba[i], tex.PixelsRgba[i + 1], tex.PixelsRgba[i + 2], tex.PixelsRgba[i + 3]);
        }

        // 眼睛：第二色 = changeBrightness(-75)，B 通道按 -75*8/7 截断 = -85
        var eye = (Math.Clamp(look.EyeColor.R, 0, 255), Math.Clamp(look.EyeColor.G, 0, 255), Math.Clamp(look.EyeColor.B, 0, 255));
        var dark = (Math.Clamp(eye.Item1 - 75, 0, 255), Math.Clamp(eye.Item2 - 75, 0, 255), Math.Clamp(eye.Item3 - 85, 0, 255));
        if (dark == eye) eye = (eye.Item1, eye.Item2, Math.Clamp(eye.Item3 + 10, 0, 255));
        Swap(276, (eye.Item1, eye.Item2, eye.Item3, 255));
        Swap(277, (dark.Item1, dark.Item2, dark.Item3, 255));
        // 皮肤三色 / 鞋四色
        for (var k = 0; k < 3; k++) Swap(260 + k, Pal(skinPal, k, look.Skin));
        for (var k = 0; k < 4; k++) Swap(268 + k, Pal(shoePal, k, look.Shoes));

        // 袖子：有袖取衬衫染色像素，无袖换皮肤色
        var shx = look.ShirtIndex * 8 % 128;
        var shy = look.ShirtIndex * 8 / 128 * 32;
        var shirtColor = look.ShirtPrismatic ? Prismatic : look.ShirtColor;
        var hasSleeves = look.ShirtId is null
            || !SleeveTable(data).TryGetValue(look.ShirtId, out var hs) || hs;
        var shirtInSheet = shx + 128 + 8 <= shirts.Width && shy + 8 <= shirts.Height;
        for (var k = 0; k < 3; k++)
        {
            if (!hasSleeves || !shirtInSheet)
            {
                Swap(256 + k, Pal(skinPal, k, look.Skin));
                continue;
            }
            var dye = Pix(shirts, shx + 128, shy + 4 - k);
            var src = dye.A < 255 ? Pix(shirts, shx, shy + 4 - k) : dye;
            var tint = dye.A < 255 ? (R: 255, G: 255, B: 255) : shirtColor;
            Swap(256 + k, (src.R * tint.R / 255, src.G * tint.G / 255, src.B * tint.B / 255, 255));
        }

        var canvas = new byte[CellW * CellH * 4];

        void Blend(int dx, int dy, int r, int g, int b, int a)
        {
            if (a == 0 || dx < 0 || dy < 0 || dx >= CellW || dy >= CellH) return;
            var di = (dy * CellW + dx) * 4;
            if (a == 255)
            {
                canvas[di] = (byte)r; canvas[di + 1] = (byte)g; canvas[di + 2] = (byte)b; canvas[di + 3] = 255;
                return;
            }
            var sa = a / 255.0;
            var da = canvas[di + 3] / 255.0;
            var oa = sa + da * (1 - sa);
            if (oa <= 0) return;
            canvas[di] = (byte)((r * sa + canvas[di] * da * (1 - sa)) / oa);
            canvas[di + 1] = (byte)((g * sa + canvas[di + 1] * da * (1 - sa)) / oa);
            canvas[di + 2] = (byte)((b * sa + canvas[di + 2] * da * (1 - sa)) / oa);
            canvas[di + 3] = (byte)(oa * 255);
        }

        void Tile(DecodedTexture tex, int sx, int sy, int w, int h, int dx, int dy, (int R, int G, int B) tint, bool tinted)
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var tx = sx + x;
                    var ty = sy + y;
                    if (tx < 0 || ty < 0 || tx >= tex.Width || ty >= tex.Height) continue;
                    var i = (ty * tex.Width + tx) * 4;
                    var a = (int)tex.PixelsRgba[i + 3];
                    if (a == 0) continue;
                    int r = tex.PixelsRgba[i], g = tex.PixelsRgba[i + 1], b = tex.PixelsRgba[i + 2];
                    if (tinted) { r = r * tint.R / 255; g = g * tint.G / 255; b = b * tint.B / 255; }
                    Blend(dx + x, dy + y, r, g, b, a);
                }
        }

        // ① 身体（frame 0 = 正面站立）
        Tile(new DecodedTexture(px, W, sheet.Height), 0, 0, 16, 32, Ox, Oy, (255, 255, 255), false);

        // ② 裤子：pantsItem 样式，同格叠乘染色
        var pbx = look.PantsIndex % 10 * 192 + (look.Male ? 0 : 96);
        var pby = look.PantsIndex / 10 * 688;
        Tile(pants, pbx, pby, 16, 32, Ox, Oy, look.PantsPrismatic ? Prismatic : look.PantsColor, true);

        // ③ 衬衫：本色层 + 染色层，正面 (4,15)
        Tile(shirts, shx, shy, 8, 8, Ox + 4, Oy + 15, (255, 255, 255), false);
        Tile(shirts, shx + 128, shy, 8, 8, Ox + 4, Oy + 15, shirtColor, true);

        // ④ 头发与配件：先解析戴帽时的受遮/隐藏发型
        var num = look.Hair;
        if (look.HatIndex >= 0)
        {
            if (look.HatHairDrawType == 2) num = -1;
            else if (look.HatHairDrawType == 1) num = ObscuredHair(num);
        }
        var hairTable = HairTable(data);
        HairMeta? meta = num >= 0 && hairTable.TryGetValue(num, out var m0) ? m0 : null;
        if (look.HatIndex >= 0 && look.HatHairDrawType == 1 && meta is { Covered: >= 0 })
        {
            num = meta.Covered;
            meta = hairTable.TryGetValue(num, out var m1) ? m1 : null;
        }
        var hairTint = look.HairPrismatic ? Prismatic : look.HairColor;
        var facial = look.Accessory >= 0 && (look.Accessory < 6 || (look.Accessory >= 19 && look.Accessory <= 22));
        var accBelow = look.Accessory >= 0 && (look.Accessory < 8 || facial);

        void DrawAccessory()
        {
            if (look.Accessory < 0) return;
            Tile(accessories, look.Accessory * 16 % accessories.Width, look.Accessory * 16 / accessories.Width * 32,
                16, 16, Ox, Oy + 2, facial ? hairTint : (255, 255, 255), facial);
        }

        void DrawHair()
        {
            if (num < 0) return;
            DecodedTexture tex;
            int sx, sy;
            if (meta is not null)
            {
                tex = Load(gamePath, meta.Tex, look.Male);
                sx = meta.TileX * 16;
                sy = meta.TileY * 16;
            }
            else
            {
                tex = Load(gamePath, "hairstyles", look.Male);
                sx = num * 16 % tex.Width;
                sy = num * 16 / tex.Width * 96;
            }
            // 越界 = 1.6 无效发型号，游戏夹到透明边，不画
            if (sx + 16 > tex.Width || sy + 32 > tex.Height) return;
            var dy = 1 + (look.Male && num >= 16 ? -1 : !look.Male && num < 16 ? 1 : 0);
            Tile(tex, sx, sy, 16, 32, Ox, Oy + dy, hairTint, true);
        }

        if (accBelow) DrawAccessory();
        DrawHair();
        if (!accBelow) DrawAccessory();

        // ⑤ 帽子：正面段，外扩 2 像素，随发型微调纵向
        if (look.HatIndex >= 0)
        {
            var hx = look.HatIndex * 20 % hats.Width;
            var hy = look.HatIndex * 20 / hats.Width * 80;
            if (hx + 20 <= hats.Width && hy + 20 <= hats.Height)
            {
                var off = look.HatIgnoreHairOffset ? 0 : HatOffset[((look.Hair % 16) + 16) % 16];
                Tile(hats, hx, hy, 20, 20, Ox - 2, Oy - 2 + (off + 2) / 4,
                    look.HatPrismatic ? Prismatic : (255, 255, 255), look.HatPrismatic);
            }
        }

        // ⑥ 手臂层：站立帧 AnimationFrame.armOffset=6 → 底图第 6 列（x96），
        //    游戏在头发/帽子之后再画（FarmerSpriteLayers.Arms 层序最高）
        Tile(new DecodedTexture(px, W, sheet.Height), 6 * 16, 0, 16, 32, Ox, Oy, (255, 255, 255), false);

        var png = PixelKit.CropScalePng(new DecodedTexture(canvas, CellW, CellH),
            0, 0, CellW, CellH, CellW * Scale, CellH * Scale);
        return png.Length == 0 ? null : "data:image/png;base64," + Convert.ToBase64String(png);
    }

    /// <summary>戴「受遮发型」帽时的发型映射，逐条对照 Farmer.getHair。</summary>
    private static int ObscuredHair(int h) => h switch
    {
        48 => 6,
        49 => 52,
        3 => 11,
        18 or 19 or 21 or 31 => 23,
        42 => 46,
        >= 50 and <= 55 => h,
        1 or 5 or 6 or 9 or 11 or 17 or 20 or 23 or 24 or 25 or 27 or 28 or 29 or 30
            or 32 or 33 or 34 or 36 or 39 or 41 or 43 or 44 or 45 or 46 or 47 => h,
        >= 16 => h < 100 ? 30 : h,
        _ => 7,
    };

    // ─────────────── Data/Shirts · Data/HairData ───────────────

    private static Dictionary<string, bool> SleeveTable(string data)
    {
        var path = Path.Combine(data, "Shirts.xnb");
        var key = path + "|" + FileStamp(path);
        lock (Gate)
        {
            if (SleeveTables.TryGetValue(key, out var t)) return t;
        }
        var table = ParseShirts(path);
        lock (Gate) SleeveTables[key] = table;
        return table;
    }

    private static Dictionary<int, HairMeta> HairTable(string data)
    {
        var path = Path.Combine(data, "HairData.xnb");
        var key = path + "|" + FileStamp(path);
        lock (Gate)
        {
            if (HairTables.TryGetValue(key, out var t)) return t;
        }
        var table = ParseHairData(path);
        lock (Gate) HairTables[key] = table;
        return table;
    }

    /// <summary>XNB 内容区游标：7-bit 长度串 / int32 / bool / 带类型 id 的对象串。</summary>
    private sealed class Cur(byte[] b)
    {
        private int _p;
        public int U7()
        {
            int v = 0, s = 0;
            while (true)
            {
                var x = b[_p++];
                v |= (x & 0x7F) << s;
                s += 7;
                if ((x & 0x80) == 0) return v;
            }
        }
        public string Str()
        {
            var n = U7();
            var s = System.Text.Encoding.UTF8.GetString(b, _p, n);
            _p += n;
            return s;
        }
        public int I32()
        {
            var v = BitConverter.ToInt32(b, _p);
            _p += 4;
            return v;
        }
        public bool Bool() => b[_p++] != 0;
        public string? ObjStr(int stringId)
        {
            var id = U7();
            if (id == 0) return null;
            if (id != stringId) throw new InvalidDataException("XNB 串类型 id 错位");
            return Str();
        }
        public void SkipManifest(out int stringId)
        {
            var n = U7();
            stringId = 0;
            for (var i = 0; i < n; i++)
            {
                var name = Str();
                _p += 4;
                if (name.EndsWith("StringReader")) stringId = i + 1;
            }
            _ = U7(); // shared count
            _ = U7(); // root reader id
        }
    }

    /// <summary>Data/Shirts：只取 HasSleeves（袖子换色要用）。字段序对照 ShirtData 声明序。</summary>
    private static Dictionary<string, bool> ParseShirts(string path)
    {
        var content = XnbDecoder.ReadContentRegion(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("解不出 Shirts 数据");
        var c = new Cur(content);
        c.SkipManifest(out var sid);
        var n = c.I32();
        var table = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var id = c.ObjStr(sid) ?? throw new InvalidDataException("衬衫缺 id");
            _ = c.U7(); // ShirtData 对象 id
            _ = c.ObjStr(sid); // Name
            _ = c.ObjStr(sid); // DisplayName
            _ = c.ObjStr(sid); // Description
            _ = c.I32();       // Price
            _ = c.ObjStr(sid); // Texture
            _ = c.I32();       // SpriteIndex
            _ = c.ObjStr(sid); // DefaultColor
            _ = c.Bool();      // CanBeDyed
            _ = c.Bool();      // IsPrismatic
            var sleeves = c.Bool();
            _ = c.Bool();      // CanChooseDuringCharacterCustomization
            if (c.U7() != 0)   // CustomFields
            {
                var cn = c.I32();
                for (var j = 0; j < cn; j++) { _ = c.ObjStr(sid); _ = c.ObjStr(sid); }
            }
            table[id] = sleeves;
        }
        return table;
    }

    /// <summary>Data/HairData：int → "贴图/tileX/tileY/uniqueLeft/coveredIndex/isBald"。</summary>
    private static Dictionary<int, HairMeta> ParseHairData(string path)
    {
        var content = XnbDecoder.ReadContentRegion(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("解不出 HairData");
        var c = new Cur(content);
        c.SkipManifest(out var sid);
        var n = c.I32();
        var table = new Dictionary<int, HairMeta>();
        for (var i = 0; i < n; i++)
        {
            var k = c.I32();
            var v = c.ObjStr(sid);
            if (v is null) continue;
            var f = v.Split('/');
            if (f.Length < 3) continue;
            table[k] = new HairMeta(
                f[0],
                int.TryParse(f[1], out var x) ? x : 0,
                int.TryParse(f[2], out var y) ? y : 0,
                f.Length > 4 && int.TryParse(f[4], out var cov) ? cov : -1);
        }
        return table;
    }
}
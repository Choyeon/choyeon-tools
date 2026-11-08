namespace ChoyeonTools.Services;

/// <summary>
/// 把固件里的厂商代号还原成看得懂的品牌。CPUID 厂商串、SMBIOS 厂商名、EDID 三字符 PNP 代码
/// 都是缩写或公司全称，直接展示要么读不出品牌（AuthenticAMD、SKG），要么太长（Micro-Star International Co., Ltd.）。
/// 这里统一保留原文，只在其前面补一个中文品牌，避免"识别成品牌"反而丢了可核对的线索。
/// </summary>
internal static class Vendors
{
    /// <summary>CPUID 的厂商串是固定的 12 字符，按全等匹配。</summary>
    private static readonly (string Raw, string Brand)[] CpuIdVendors =
    [
        ("GenuineIntel", "英特尔"),
        ("AuthenticAMD", "AMD"),
        ("HygonGenuine", "海光"),
        ("CentaurHauls", "威盛"),
        ("VIA VIA VIA", "威盛"),
        ("Genuine  RDC", "RDC"),
        ("UMC UMC UMC ", "UMC"),
    ];

    /// <summary>公司全称与常见拼法。匹配时统一转大写，所以这里的检索词也写成大写。</summary>
    private static readonly (string Needle, string Brand)[] CorporateNames =
    [
        ("ADVANCED MICRO DEVICES", "AMD"),
        ("NVIDIA", "NVIDIA"),
        ("INTEL CORPORATION", "英特尔"),
        ("INTEL", "英特尔"),
        ("ASUSTEK", "华硕"),
        ("ASUS", "华硕"),
        ("MICRO-STAR", "微星"),
        ("MICRO STAR INTERNATIONAL", "微星"),
        ("RAPOO", "雷柏"),
        ("ASMEDIA", "祥硕"),
        ("GENESYS LOGIC", "创惟"),
        ("GIGABYTE", "技嘉"),
        ("ASROCK", "华擎"),
        ("BIOSTAR", "映泰"),
        ("ELITEGROUP", "精英"),
        ("AMERICAN MEGATRENDS", "AMI"),
        ("PHOENIX TECHNOLOGIES", "Phoenix"),
        ("INSYDE", "Insyde"),
        ("MEDIATEK", "联发科"),
        ("MEDIA TEK", "联发科"),
        ("RAZER", "雷蛇"),
        ("REALTEK", "瑞昱"),
        ("SAMSUNG", "三星"),
        ("MICRON", "美光"),
        ("SK HYNIX", "SK 海力士"),
        ("HYNIX SEMICONDUCTOR", "SK 海力士"),
        ("TOSHIBA", "东芝"),
        ("WESTERN DIGITAL", "西部数据"),
        ("SEAGATE", "希捷"),
        ("KINGSTON", "金士顿"),
        ("SMART MODULES", "金士顿"),
        ("MAXSUN", "铭瑄"),
        ("KINGBANK", "金百达"),
        ("JINGYUE", "精粤"),
        ("GLOWAY", "光威"),
        ("BIWIN", "佰维"),
        ("LONGSYS", "江波龙"),
        ("FORESEE", "江波龙"),
        ("NETAC", "朗科"),
        ("AIGO", "爱国者"),
        ("RAMAXEL", "记忆科技"),
        ("TEAM GROUP", "十铨"),
        ("APACER", "宇瞻"),
        ("INNODISK", "宜鼎"),
        ("SILICON MOTION", "慧荣"),
        ("CXMT", "长鑫存储"),
        ("CHANGXIN", "长鑫存储"),
        ("YMTC", "长江存储"),
        ("YANGTZE MEMORY", "长江存储"),
        ("COLORFUL", "七彩虹"),
        ("CARDEXPERT", "耕升"),
        ("GAINWARD", "耕升"),
        ("ZOTAC", "索泰"),
        ("GALAX", "影驰"),
        ("GALAXY", "影驰"),
        ("KFA2", "影驰"),
        ("INNO3D", "映众"),
        ("LEADTEK", "丽台"),
        ("SPARKLE", "旌宇"),
        ("GUNNIR", "蓝戟"),
        ("MANLI", "万丽"),
        ("ELSA", "艾尔莎"),
        ("PNY", "必恩威"),
        ("SAPPHIRE", "蓝宝石"),
        ("POWERCOLOR", "撼讯"),
        ("DATALAND", "迪兰"),
        ("XFX", "讯景"),
        ("YESTON", "盈通"),
        ("SOYO", "梅捷"),
        ("ONDA", "昂达"),
        ("APPLE", "苹果"),
        ("DELL", "戴尔"),
        ("HEWLETT-PACKARD", "惠普"),
        ("HEWLETT PACKARD", "惠普"),
        ("LENOVO", "联想"),
        ("PHILIPS", "飞利浦"),
        ("BENQ", "明基"),
        ("VIEWSONIC", "优派"),
        ("SONY", "索尼"),
        ("LOGITECH", "罗技"),
        ("EDIFIER", "漫步者"),
        ("MICROSOFT", "微软"),
        ("CORSAIR", "美商海盗船"),
        ("DAHUA", "大华"),
        ("HUAWEI", "华为"),
        ("XIAOMI", "小米"),
    ];

    /// <summary>
    /// EDID 的显示器 PNP 厂商是 ANSI 注册的三字符代码。这里只收常见面板与整机品牌，
    /// 不确定的代码一律原样显示，宁可少认也不认错。
    /// </summary>
    private static readonly Dictionary<string, string> MonitorPnpBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACI"] = "AOC 冠捷",
        ["ACR"] = "宏碁",
        ["APP"] = "苹果",
        ["AUO"] = "友达光电",
        ["BNQ"] = "明基",
        ["BOE"] = "京东方",
        ["CMN"] = "奇美 (InnoLux)",
        ["DEL"] = "戴尔",
        ["GSM"] = "乐金 (LGD)",
        ["IBM"] = "IBM",
        ["KTC"] = "康冠",
        ["LEN"] = "联想",
        ["MSI"] = "微星",
        ["PHL"] = "飞利浦",
        ["SAM"] = "三星",
        ["SKG"] = "KTC 康冠",
        ["SNY"] = "索尼",
        ["VSC"] = "优派",
        ["XMI"] = "小米",
    };

    /// <summary>厂商字段通用入口：CPUID 串、SMBIOS/驱动的厂商名都能直接传进来。</summary>
    public static string Brand(string? raw)
    {
        string text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        foreach ((string code, string brand) in CpuIdVendors)
        {
            if (text.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return Annotate(brand, text);
            }
        }

        string upper = text.ToUpperInvariant();
        foreach ((string needle, string brand) in CorporateNames)
        {
            if (upper.Contains(needle, StringComparison.Ordinal))
            {
                return Annotate(brand, text);
            }
        }

        return text;
    }

    /// <summary>显示器 EDID 的三字符 PNP 代码：常见品牌用精选中文名，其余查 UEFI 注册表数据。</summary>
    public static string MonitorPnp(string? code)
    {
        string text = code?.Trim() ?? string.Empty;
        if (text.Length != 3)
        {
            return text;
        }

        if (MonitorPnpBrands.TryGetValue(text, out var brand))
        {
            return $"{brand}（{text}）";
        }

        return NameTable.Pnp.Find(text) is { } name ? $"{name}（{text}）" : text;
    }

    /// <summary>只要品牌部分：能映射中文品牌就取中文，否则保留原文，不带括号里的公司全称。</summary>
    public static string BrandOnly(string? raw)
    {
        string text = Brand(raw);
        int open = text.IndexOf('（');
        return open > 0 ? text[..open].Trim() : text;
    }

    /// <summary>品牌名已经出现在原文开头就不重复了，"NVIDIA Corporation" 这类直接原样返回。</summary>
    private static string Annotate(string brand, string raw) =>
        raw.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? raw : $"{brand}（{raw}）";
}

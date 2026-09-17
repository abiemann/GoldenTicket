namespace GoldenTicket.Vision;

/// <summary>
/// Center and printed direction of one physical train space on the upright classic-US board.
/// X/Y are fractions of the complete rectified board; tangent components are a unit vector
/// in 1996 × 1248 board-pixel axes. The orientation is undirected.
/// </summary>
public readonly record struct BoardSlotPoint(double X, double Y, double TangentX, double TangentY)
{
    public double ReferenceX => X * ClassicUsRouteGeometry.ReferenceWidth;
    public double ReferenceY => Y * ClassicUsRouteGeometry.ReferenceHeight;
}

/// <summary>
/// Digitized physical train-space centers for the upright classic-US board. Only inspected
/// lanes are included. Route IDs are the pinned classic-US manifest keys; the rules manifest
/// itself remains independent of camera/presentation geometry.
/// </summary>
public static class ClassicUsRouteGeometry
{
    public const string ProfileId = "ttr-us-classic-en-v1";
    public const string GeometryVersion = "classic-us-slots-2026-09-16-v1";
    // The original 3456 × 2160 empty-board photo, before scaling to the 1996 × 1248
    // reference used below. Kept as provenance; the photo is user-supplied, not shipped.
    public const string ReferencePhotoSha256 =
        "9b7746396ff559f932c1578c3d3c662ad1f79cffb04e6b4261e790e2ab9e238d";
    public const double ReferenceWidth = 1996;
    public const double ReferenceHeight = 1248;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<BoardSlotPoint>> Slots =
        new Dictionary<string, IReadOnlyList<BoardSlotPoint>>(StringComparer.Ordinal)
        {
            ["atlanta--charleston"] = Lane((1616, 811), (1684, 811)),
            ["atlanta--miami"] = Lane((1587, 845), (1630, 895), (1674, 946), (1717, 996), (1763, 1045)),
            ["atlanta--nashville"] = One(1508, 754, .84, .54),
            ["atlanta--new-orleans--a"] = Lane((1508, 816), (1460, 866), (1417, 921), (1379, 977)),
            ["atlanta--new-orleans--b"] = Lane((1534, 832), (1484, 880), (1442, 936), (1402, 990)),
            ["atlanta--raleigh--a"] = Lane((1586, 755), (1643, 712)),
            ["atlanta--raleigh--b"] = Lane((1605, 770), (1656, 727)),
            ["boston--montreal--a"] = Lane((1796, 176), (1841, 214)),
            ["boston--montreal--b"] = Lane((1779, 192), (1829, 234)),
            ["boston--new-york--a"] = Lane((1838, 299), (1804, 357)),
            ["boston--new-york--b"] = Lane((1863, 306), (1829, 364)),
            ["calgary--helena"] = Lane((507, 206), (550, 254), (595, 307), (638, 359)),
            ["calgary--seattle"] = Lane((265, 289), (335, 283), (398, 260), (443, 210)),
            ["calgary--vancouver"] = Lane((268, 184), (337, 176), (405, 169)),
            ["calgary--winnipeg"] = Lane((518, 142), (580, 125), (647, 114), (716, 114), (782, 124), (849, 144)),
            ["charleston--miami"] = Lane((1742, 847), (1754, 910), (1769, 972), (1787, 1034)),
            ["charleston--raleigh"] = Lane((1721, 711), (1741, 763)),
            ["chicago--duluth"] = Lane((1166, 429), (1231, 456), (1297, 479)),
            ["chicago--omaha"] = Lane((1110, 538), (1165, 510), (1237, 496), (1305, 503)),
            ["chicago--pittsburgh--a"] = Lane((1405, 474), (1477, 462), (1549, 460)),
            ["chicago--pittsburgh--b"] = Lane((1409, 492), (1478, 483), (1549, 479)),
            ["chicago--saint-louis--a"] = Lane((1310, 546), (1282, 599)),
            ["chicago--saint-louis--b"] = Lane((1336, 558), (1305, 612)),
            ["chicago--toronto"] = Lane((1373, 466), (1412, 432), (1465, 395), (1536, 353)),
            ["dallas--el-paso"] = Lane((849, 1013), (925, 1000), (1002, 987), (1079, 975)),
            ["dallas--houston--a"] = One(1128, 1014, 1, 1),
            ["dallas--houston--b"] = One(1148, 994, 1, 1),
            ["dallas--little-rock"] = Lane((1150, 910), (1207, 852)),
            ["dallas--oklahoma-city--a"] = Lane((1080, 852), (1087, 920)),
            ["dallas--oklahoma-city--b"] = Lane((1104, 851), (1113, 919)),
            ["denver--helena"] = Lane((692, 459), (716, 522), (739, 584), (761, 645)),
            ["denver--kansas-city--a"] = Lane((840, 691), (909, 689), (975, 674), (1033, 650)),
            ["denver--kansas-city--b"] = Lane((837, 715), (907, 716), (974, 700), (1037, 676)),
            ["denver--oklahoma-city"] = Lane((812, 742), (876, 779), (945, 798), (1010, 803)),
            ["denver--omaha"] = Lane((816, 646), (869, 607), (936, 587), (1007, 569)),
            ["denver--phoenix"] = Lane((553, 894), (586, 831), (632, 777), (683, 730), (733, 701)),
            ["denver--salt-lake-city--a"] = Lane((572, 613), (640, 617), (706, 637)),
            ["denver--salt-lake-city--b"] = Lane((572, 637), (640, 643), (705, 663)),
            ["denver--santa-fe"] = Lane((763, 745), (760, 808)),
            ["duluth--helena"] = Lane((725, 402), (798, 401), (870, 401), (943, 399), (1015, 398), (1085, 396)),
            ["duluth--omaha--a"] = Lane((1082, 443), (1067, 510)),
            ["duluth--omaha--b"] = Lane((1107, 447), (1091, 514)),
            ["duluth--sault-st-marie"] = Lane((1184, 371), (1253, 340), (1316, 307)),
            ["duluth--toronto"] = Lane((1182, 385), (1254, 370), (1328, 356), (1401, 341), (1475, 327), (1546, 318)),
            ["duluth--winnipeg"] = Lane((948, 219), (995, 271), (1044, 322), (1091, 370)),
            ["el-paso--houston"] = Lane((804, 1046), (869, 1073), (938, 1091), (1007, 1093), (1077, 1084), (1147, 1061)),
            ["el-paso--los-angeles"] = Lane((337, 970), (405, 1003), (474, 1025), (543, 1039), (613, 1042), (685, 1034)),
            ["el-paso--oklahoma-city"] = Lane((799, 1000), (866, 979), (929, 945), (980, 905), (1025, 855)),
            ["el-paso--phoenix"] = Lane((578, 962), (641, 986), (707, 1005)),
            ["el-paso--santa-fe"] = Lane((759, 898), (756, 966)),
            ["helena--omaha"] = Lane((737, 440), (801, 463), (868, 486), (933, 510), (996, 533)),
            ["helena--salt-lake-city"] = Lane((632, 453), (584, 516), (552, 578)),
            ["helena--seattle"] = Lane((264, 325), (332, 341), (401, 359), (467, 372), (534, 386), (599, 399)),
            ["helena--winnipeg"] = Lane((706, 357), (754, 314), (804, 273), (856, 230)),
            ["houston--new-orleans"] = Lane((1240, 1034), (1308, 1030)),
            ["kansas-city--oklahoma-city--a"] = Lane((1090, 702), (1074, 763)),
            ["kansas-city--oklahoma-city--b"] = Lane((1116, 706), (1100, 765)),
            ["kansas-city--omaha--a"] = One(1074, 611, .49, .87),
            ["kansas-city--omaha--b"] = One(1103, 603, .49, .87),
            ["kansas-city--saint-louis--a"] = Lane((1154, 640), (1220, 638)),
            ["kansas-city--saint-louis--b"] = Lane((1154, 663), (1220, 663)),
            ["las-vegas--los-angeles"] = Lane((363, 838), (315, 879)),
            ["las-vegas--salt-lake-city"] = Lane((460, 802), (495, 743), (515, 680)),
            ["little-rock--nashville"] = Lane((1290, 812), (1360, 790), (1420, 752)),
            ["little-rock--new-orleans"] = Lane((1261, 860), (1301, 918), (1336, 977)),
            ["little-rock--oklahoma-city"] = Lane((1123, 810), (1192, 810)),
            ["little-rock--saint-louis"] = Lane((1267, 705), (1250, 766)),
            ["los-angeles--phoenix"] = Lane((334, 920), (405, 920), (473, 923)),
            ["los-angeles--san-francisco--a"] = Lane((150, 794), (184, 855), (235, 908)),
            ["los-angeles--san-francisco--b"] = Lane((175, 792), (210, 845), (257, 899)),
            ["miami--new-orleans"] = Lane((1437, 1001), (1500, 978), (1567, 972), (1633, 987), (1695, 1025), (1754, 1070)),
            ["montreal--new-york"] = Lane((1730, 220), (1739, 282), (1751, 345)),
            ["montreal--sault-st-marie"] = Lane((1406, 225), (1469, 200), (1533, 175), (1599, 157), (1667, 149)),
            ["montreal--toronto"] = Lane((1690, 176), (1629, 214), (1584, 264)),
            ["nashville--pittsburgh"] = Lane((1439, 682), (1477, 626), (1532, 584), (1589, 540)),
            ["nashville--raleigh"] = Lane((1491, 691), (1562, 666), (1630, 660)),
            ["nashville--saint-louis"] = Lane((1321, 689), (1385, 701)),
            ["new-york--pittsburgh--a"] = Lane((1655, 425), (1724, 399)),
            ["new-york--pittsburgh--b"] = Lane((1669, 454), (1731, 419)),
            ["new-york--washington--a"] = Lane((1778, 448), (1779, 512)),
            ["new-york--washington--b"] = Lane((1807, 448), (1808, 512)),
            ["oklahoma-city--santa-fe"] = Lane((831, 846), (898, 837), (967, 828)),
            ["phoenix--santa-fe"] = Lane((584, 919), (646, 894), (711, 867)),
            ["pittsburgh--raleigh"] = Lane((1637, 550), (1662, 612)),
            ["pittsburgh--saint-louis"] = Lane((1317, 642), (1380, 613), (1442, 582), (1504, 552), (1565, 515)),
            ["pittsburgh--toronto"] = Lane((1596, 367), (1605, 426)),
            ["pittsburgh--washington"] = Lane((1674, 509), (1740, 537)),
            ["portland--salt-lake-city"] = Lane((217, 398), (284, 419), (349, 447), (408, 481), (455, 530), (501, 583)),
            ["portland--san-francisco--a"] = Lane((124, 427), (109, 493), (101, 563), (103, 631), (114, 700)),
            ["portland--san-francisco--b"] = Lane((151, 431), (128, 496), (125, 563), (128, 632), (137, 696)),
            ["portland--seattle--a"] = One(172, 338, -.423, .906),
            ["portland--seattle--b"] = One(192, 344, -.423, .906),
            ["raleigh--washington--a"] = Lane((1712, 647), (1751, 592)),
            ["raleigh--washington--b"] = Lane((1729, 655), (1774, 609)),
            ["salt-lake-city--san-francisco--a"] = Lane((201, 713), (269, 692), (335, 671), (402, 652), (466, 630)),
            ["salt-lake-city--san-francisco--b"] = Lane((205, 737), (275, 715), (341, 692), (406, 672), (470, 650)),
            ["sault-st-marie--toronto"] = Lane((1454, 283), (1528, 296)),
            ["sault-st-marie--winnipeg"] = Lane((975, 184), (1041, 199), (1109, 213), (1177, 223), (1243, 236), (1314, 251)),
            ["seattle--vancouver--a"] = One(195, 243, 0, 1),
            ["seattle--vancouver--b"] = One(217, 243, 0, 1),
        };

    public static int RouteCount => Slots.Count;
    public static int SlotCount => Slots.Values.Sum(points => points.Count);
    public static IReadOnlyCollection<string> RouteIds => Slots.Keys.ToArray();

    public static bool TryGetSlots(string routeId, out IReadOnlyList<BoardSlotPoint> slots) =>
        Slots.TryGetValue(routeId, out slots!);

    private static BoardSlotPoint[] Lane(params (double X, double Y)[] centers)
    {
        if (centers.Length == 0) throw new ArgumentException("A measured lane needs a train space.", nameof(centers));
        var result = new BoardSlotPoint[centers.Length];
        for (var index = 0; index < centers.Length; index++)
        {
            var start = centers[Math.Max(0, index - 1)];
            var end = centers[Math.Min(centers.Length - 1, index + 1)];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            result[index] = new BoardSlotPoint(centers[index].X / ReferenceWidth,
                centers[index].Y / ReferenceHeight,
                length > 0 ? dx / length : 1, length > 0 ? dy / length : 0);
        }
        return result;
    }

    private static BoardSlotPoint[] One(double x, double y, double tangentX, double tangentY)
    {
        var length = Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
        if (length < .01) throw new ArgumentException("A single train space needs a measured direction.");
        return [new BoardSlotPoint(x / ReferenceWidth, y / ReferenceHeight,
            tangentX / length, tangentY / length)];
    }
}

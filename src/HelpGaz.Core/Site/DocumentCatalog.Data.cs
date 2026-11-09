namespace HelpGaz.Core.Site;

/// <summary>Документ со страницы ИТД: маршрут генерации и хэши нормализованных имён файла и шаблона.</summary>
public sealed record CatalogEntry(string Url, string DownloadHash, string TemplateHash, bool Binary, bool CheckValues);

/// <summary>
/// Таблица собрана из кода сайта скриптом tools/build_catalog.py. Имена хранятся хэшами: репозиторий публичный,
/// а часть имён документов содержит фамилии. Настоящие имена программа получает с сайта уже после входа.
/// </summary>
public static partial class DocumentCatalog
{
    private static readonly CatalogEntry[] Entries =
    {
        new("/Titul", "c977476ca1e71e2796a0e355", "c977476ca1e71e2796a0e355", false, true),
        new("/Endtitul", "6c1f6e6b1dc07d799d015bb8", "bd85ec29db47680d96ca2439", false, true),
        new("/Aktpriemki", "826f49b3bacd823462af8b23", "ceeb5515807094ff9031888a", false, true),
        new("/Journal", "4758dcb00cb1fcffa89ae6f9", "a716d5ffe12fd49805596732", false, true),
        new("/Sxemastikov", "16164ab0770d053f5a9435ce", "e99fa4b5084c7246de8695c3", true, true),
        new("/CtrpasportNaruj", "e17b6b6dc41434aa997b0057", "e17b6b6dc41434aa997b0057", false, true),
        new("/CtrpasportVnutr", "e27f50338e3ff1bd886519ba", "e27f50338e3ff1bd886519ba", false, true),
        new("/VikZn", "03ef3185f1fa84f453435647", "03ef3185f1fa84f453435647", false, true),
        new("/VikGas", "4c4b14df7b0ced0c09ed6e91", "4c4b14df7b0ced0c09ed6e91", false, true),
        new("/VikRd", "fb062ccd1e053339ca235165", "fb062ccd1e053339ca235165", false, true),
        new("/Mex20", "296d7adf8b735c2422e095fb", "296d7adf8b735c2422e095fb", false, true),
        new("/Mex25", "d88cc61654abee6bceb7b6aa", "d88cc61654abee6bceb7b6aa", false, true),
        new("/Mex63", "83c83028d30ee2c85bd9e4fe", "83c83028d30ee2c85bd9e4fe", false, true),
        new("/Aktgerm", "0f873fbaf664d877b29f971d", "0f873fbaf664d877b29f971d", false, true),
        new("/Aktgerm/fresh", "d6eb49358c551c6f6917648a", "0f873fbaf664d877b29f971d", false, false),
        new("/Aktsignal", "be2856b19be98a6ab4c57e7f", "be2856b19be98a6ab4c57e7f", false, true),
        new("/Wdpo", "600819841b868a7666b3417a", "600819841b868a7666b3417a", false, true),
        new("/Zazeml", "7a9a7166921d2a07d7b3973e", "7a9a7166921d2a07d7b3973e", false, true),
        new("/Aktrazbivki", "ae263b56b9e186dd978317aa", "ae263b56b9e186dd978317aa", false, true),
        new("/Aktskritix", "be3e5d8d2783cf01933ef533", "be3e5d8d2783cf01933ef533", false, true),
        new("/Aktobokazanii", "cbd194d8dae5a067ec837a0d", "cbd194d8dae5a067ec837a0d", false, true),
        new("/Aktpriemaperedachi", "325ac8b4cef4309d54db4be1", "325ac8b4cef4309d54db4be1", false, true),
        new("/Aktvxodnogo", "71cfe9bfb7a90fcf7679974e", "2521dcbb2d1d286a69c4fdf9", false, true),
        new("/AktgermetizaciiVvodov", "c7c449d0ac5aad68aba410ee", "c7c449d0ac5aad68aba410ee", false, true),
        new("/AktsverleniyaLyukov", "64a9154f34bcab01cb2df55c", "8affda0835908b9cbaf084f9", false, true),
        new("/Aktskritix25", "f70ea7e663d72bcedd792732", "f70ea7e663d72bcedd792732", false, true),
        new("/Aktskritix26", "3deb89b604f5454cfdd57052", "3deb89b604f5454cfdd57052", false, true),
        new("/Aktskritix27", "a97a87c0e6bc64d6ad9ede18", "a97a87c0e6bc64d6ad9ede18", false, true),
        new("/Aktskritix28", "104133db11f0ea487e6e9c80", "104133db11f0ea487e6e9c80", false, true),
        new("/Aktskritix29", "cce65b0462afce0eabfdfd34", "cce65b0462afce0eabfdfd34", false, true),
        new("/Aktskritix30", "2f042ada08798a13e3656048", "2f042ada08798a13e3656048", false, true),
        new("/Aktskritix31", "3a5da775ba448e188e1bc595", "3a5da775ba448e188e1bc595", false, true),
        new("/Aktskritix32", "31129437c8dcc3668f04d02c", "31129437c8dcc3668f04d02c", false, true),
        new("/Aktskritix33", "f4e6bd8f686df151e0f21561", "7b22fc58492c17e725ebf1d7", false, true),
        new("/Aktskritix34", "5a8c1eaaf5431ec294a68d90", "5a8c1eaaf5431ec294a68d90", false, true),
        new("/Aktskritix35", "cf2e2f17786699c1012de0b8", "cf2e2f17786699c1012de0b8", false, true),
        new("/Aktskritix36", "c84b728c2ff32c3894ab11d4", "c84b728c2ff32c3894ab11d4", false, true),
        new("/Aktskritix37", "aef582fb7328bcceded40247", "aef582fb7328bcceded40247", false, true),
        new("/Akrpuskonaladki", "ac25fad55eb0384f4cc93a6b", "c1d544d634ba23a1d460a377", false, true),
        new("/Dogovortexobs", "5df382f47eb94022d4da0174", "17b41fae025d6f7eea47d2a3", false, true),
        new("/wurnalavtorskogo", "54d46084bc12b192bab70aa2", "53a5d7bfaff8a13a73585881", false, true),
        new("/Nehaev", "99ca7e985ba5e7d66587179b", "42b9f7e8fcad1085dae9b6ac", false, true),
        new("/Wadrinov", "a0c20f42fdf1bb703d888d5c", "e96f1d882020aee2706d5ddc", false, true),
        new("/PismoNedikovu", "0830079cb1e8ffeaa36bd7e5", "88d07d81139ac713fc0f0761", false, true),
        new("/Proverkaprovoda", "8f3e319d2dbdaf996748d99b", "a5a8d06047f873c27ff2cd52", false, true),
        new("/Uvedomleniewaxbaz", "3d0d6d6fbde39a2c04ba39b8", "8f94714e1e6ef0a5653a3270", false, true),
        new("/Doverenostmklien", "88561237983aeb769f72f84c", "357731f5196ff584a8448918", false, true),
    };
}

"""Rebuilds src/HelpGaz.Core/Site/DocumentCatalog.Data.cs from the source of the site.

Usage: python tools/build_catalog.py <path to KssWorkSite>
Names are stored only as hashes: this repository is public and some document names contain surnames.
"""
import hashlib
import re
import sys
from pathlib import Path

site = Path(sys.argv[1])
itd = (site / "Frontend/src/components/ITD/ITD.jsx").read_text(encoding="utf-8")
server = (site / "Backend/server.js").read_text(encoding="utf-8")

config = re.findall(r"'([^']+)':\s*\{\s*url:\s*`([^`]+)`,\s*fileName:\s*'([^']+)'\s*\}", itd)
requires = dict(re.findall(r"const\s+(\w+)\s*=\s*require\('\./routes/([^']+)'\)", server))
mounts = {path: var for path, var in re.findall(r"app\.use\('([^']+)',\s*(?:[\w.]+,\s*)*(\w+)\)", server)}
template_call = re.compile(
    r"(?:renderDocx|templatePath'\)|createSimpleDocRoute|createMexRoute|createHiddenWorksRoute|createSimpleHiddenWorksRoute)"
    r"\s*\(\s*\n?\s*['\"]([^'\"]+\.(?:docx|dwg))['\"]"
)


def digest(name):
    normalized = re.sub(r"\s+", " ", name.lower().replace("ё", "е")).strip()
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()[:24]


lines = []
for _name, url, download in config:
    if url.startswith("/itd-rows"):
        continue
    base = "/" + url.split("/")[1]
    route = requires[mounts[base]]
    source = (site / "Backend/routes" / (route if route.endswith(".js") else route + ".js")).read_text(encoding="utf-8")
    found = template_call.search(source) or re.search(r"['\"]([^'\"]+\.(?:docx|dwg))['\"]", source)
    template = found.group(1)
    if not (site / "Backend/templates" / template).exists():
        raise SystemExit(f"Template for {url} not found")
    binary = "true" if download.lower().endswith(".dwg") else "false"
    check_values = "false" if url.endswith("/fresh") else "true"
    lines.append(f'        new("{url}", "{digest(download)}", "{digest(template)}", {binary}, {check_values}),')

output = Path(__file__).parent.parent / "src/HelpGaz.Core/Site/DocumentCatalog.Data.cs"
output.write_text(
    "namespace HelpGaz.Core.Site;\n\n"
    "/// <summary>Документ со страницы ИТД: маршрут генерации и хэши нормализованных имён файла и шаблона.</summary>\n"
    "public sealed record CatalogEntry(string Url, string DownloadHash, string TemplateHash, bool Binary, bool CheckValues);\n\n"
    "/// <summary>\n"
    "/// Таблица собрана из кода сайта скриптом tools/build_catalog.py. Имена хранятся хэшами: репозиторий публичный,\n"
    "/// а часть имён документов содержит фамилии. Настоящие имена программа получает с сайта уже после входа.\n"
    "/// </summary>\n"
    "public static partial class DocumentCatalog\n{\n"
    "    private static readonly CatalogEntry[] Entries =\n    {\n" + "\n".join(lines) + "\n    };\n}\n",
    encoding="utf-8",
)
print(len(lines), "entries written")

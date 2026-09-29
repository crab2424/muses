# Artifact 公開用: index.html から doctype/html/head/body タグを外した版を _artifact/index.html に書き出す
# （Artifact 側がスケルトンを被せるため）。他のファイルはそのまま files で一緒に公開する。
import re, pathlib
src = pathlib.Path(__file__).with_name('index.html').read_text()
out = re.sub(r'<!doctype html>\s*|</?html[^>]*>\s*|</?head>\s*|</?body>\s*|<meta charset[^>]*>\s*|<meta name="viewport"[^>]*>\s*', '', src, flags=re.I)
d = pathlib.Path(__file__).with_name('_artifact'); d.mkdir(exist_ok=True)
(d / 'index.html').write_text(out)
print(out[:300])

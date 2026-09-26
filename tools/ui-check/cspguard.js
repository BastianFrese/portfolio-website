/* Wächter gegen style-Attribute im ausgelieferten Markup.

   Warum ein eigener Wächter: die CSP der Seite ist
   `default-src 'none'; style-src 'self'` — ohne 'unsafe-inline'. Ein
   style-Attribut wird davon **still** verworfen. Kein Fehler, keine Meldung in
   der Konsole, kein Eintrag im Log: die Regel gilt einfach nicht. Genau so
   standen die CPU-/RAM-Balken der Knoten seit dem ersten Tag auf null Breite
   (`site.js` setzte `style="width:…"`), und zwei Abstände im Markup wirkten nie.
   Gefunden wurde das nicht durch Hinsehen, sondern durch Nachrechnen der CSP.

   Erlaubt und ausdrücklich **nicht** betroffen: `el.style.width = …`, also die
   direkte Zuweisung an die CSSOM. Blockiert sind nur das Attribut,
   `setAttribute('style', …)` und `.style.cssText`. Der Wächter unterscheidet
   genau so — er sucht ein Anführungszeichen **vor** `style`.

   Läuft nicht in der CI (wie sparktest.js): die Prüfung ist ein Skript, kein
   Testprojekt. Aufruf:  node tools/ui-check/cspguard.js
*/

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', '..');
const fail = [];
let pass = 0;
const check = (ok, label) => { if (ok) pass += 1; else fail.push(label); };

/* Ein Attribut entsteht nur dort, wo einem `style` ein Anführungszeichen
   unmittelbar vorangeht — `'" style="width:'` in einer innerHTML-Zeichenkette,
   `style="…"` in Razor. Nach einem Buchstaben oder Punkt (`fill.style.width`)
   ist es die erlaubte CSSOM-Zuweisung und wird nicht getroffen. */
const ATTRIBUTE = /["']\s*style\s*=/;

/* Kommentare zählen nicht: sie werden nie ausgeliefert, und dieser Wächter
   muss sich selbst beschreiben dürfen. Der Preis ist eine Lücke — ein Attribut
   hinter einem Zeilenkommentar am Ende einer echten Zeile bliebe unentdeckt.
   Das ist vertretbar: ausgeliefertes Markup steht nie hinter `//`. */
const COMMENT = /^\s*(\/\/|\/\*|\*|<!--|@\*)/;

function walk(dir, ext, found = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!['obj', 'bin', 'node_modules'].includes(entry.name)) walk(full, ext, found);
    } else if (entry.name.endsWith(ext)) {
      found.push(full);
    }
  }
  return found;
}

const siteJs = path.join(ROOT, 'wwwroot', 'js', 'site.js');
const targets = [siteJs, ...walk(path.join(ROOT, 'Views'), '.cshtml')];

/* Findet der Wächter nichts, prüft er nichts — und sähe trotzdem grün aus. */
check(fs.existsSync(siteJs), 'wwwroot/js/site.js nicht gefunden');
check(targets.length > 3, `zu wenige Zieldateien (${targets.length})`);

for (const file of targets) {
  const rel = path.relative(ROOT, file).replace(/\\/g, '/');
  fs.readFileSync(file, 'utf8').split('\n').forEach((text, i) => {
    if (!COMMENT.test(text) && ATTRIBUTE.test(text)) {
      fail.push(`${rel}:${i + 1}  ${text.trim()}`);
    }
  });
}

const site = fs.readFileSync(siteJs, 'utf8');

/* Dieselbe Kommentar-Regel gilt für die beiden Muster unten: der Kommentar an
   `renderNodes` nennt `setAttribute('style', …)` als das, was man NICHT tun
   darf — und wurde beim ersten Lauf prompt als Verstoß gemeldet. Ein Wächter,
   der vor sich selbst warnt, wird zu Recht ignoriert. */
const siteCode = site.split('\n').filter((l) => !COMMENT.test(l)).join('\n');

/* Der Wächter muss nachweislich anschlagen können. Ohne das wäre ein grüner
   Lauf nicht von einem kaputten Muster zu unterscheiden — und ein Wächter, der
   nie etwas findet, sieht genauso aus wie einer, der nichts zu finden hat.
   Die erste Zeile ist das Original des Fehlers, aus dem er entstanden ist. */
check(ATTRIBUTE.test('+ \'" style="width:\' + row.pct + \'%"></div>\''),
  'Muster erkennt den Originalfehler nicht');
check(!ATTRIBUTE.test('fill.style.width = row.pct + "%"'),
  'Muster hält die erlaubte CSSOM-Zuweisung für ein Attribut');
check(!ATTRIBUTE.test('  <svg viewBox="0 0 64 14" preserveAspectRatio="none">'),
  'Muster schlägt auf SVG-Attribute an');
check(COMMENT.test('  * Styling über CSS-Klassen: `style="…"` ist verboten'),
  'Kommentarzeile nicht als Kommentar erkannt');

/* Diese beiden Formen sind ebenfalls blockiert und entstehen leicht, wenn
   jemand einen style-String zusammensetzt. */
check(!/setAttribute\(\s*['"]style['"]/.test(siteCode), 'setAttribute("style", …) in site.js');
check(!/\.style\.cssText\s*=/.test(siteCode), '.style.cssText = … in site.js');

/* Die Gegenprobe: gäbe es die erlaubte Zuweisung nicht mehr, prüfte der
   Wächter eine Datei, die gar keine Balken mehr zeichnet — und wäre grün,
   obwohl die Anzeige kaputt ist. */
check(/\.style\.width\s*=/.test(siteCode), 'keine CSSOM-Breitenzuweisung mehr in site.js');

console.log(`${pass} bestanden, ${fail.length} fehlgeschlagen`);
for (const f of fail) console.log('  - ' + f);
process.exit(fail.length ? 1 : 0);

/* Prüft die Sparkline-Geometrie am AUSGELIEFERTEN Code.

   Warum so und nicht anders: die Funktionen liegen in einer IIFE und sind von
   außen nicht erreichbar, und ein Browser stand für die Prüfung nicht zur
   Verfügung. Statt die Geometrie nachzubauen — dann prüfte ich meine Kopie und
   nicht das Programm — wird der Quelltext hier aus `wwwroot/js/site.js`
   geschnitten und in Node gegen einen minimalen DOM-Stub ausgeführt.

   Der Kern ist eine einzige Unterscheidung, und sie ist der ganze Zweck der
   Messung: `null` heißt „in diesem Slot nicht gemessen" und muss als `—`
   erscheinen, `0` heißt „gemessen, und da war nichts" und muss als `0`
   erscheinen. Die beiden zu verwechseln wäre die eine Falschaussage, die diese
   Anzeige nicht machen darf.

   Aufruf:  node tools/ui-check/sparktest.js
*/

const fs = require('fs');
const path = require('path');

const FILE = path.join(__dirname, '..', '..', 'wwwroot', 'js', 'site.js');
const src = fs.readFileSync(FILE, 'utf8');

/* Von der Markierung bis zur schließenden Klammer auf Tiefe 0. */
function grab(marker) {
  const start = src.indexOf(marker);
  if (start < 0) throw new Error('nicht gefunden: ' + marker);
  let depth = 0, i = src.indexOf('{', start), end = -1;
  for (; i < src.length; i += 1) {
    if (src[i] === '{') depth += 1;
    else if (src[i] === '}') { depth -= 1; if (depth === 0) { end = i; break; } }
  }
  if (end < 0) throw new Error('unbalanciert: ' + marker);
  return src.slice(start, end + 1) + ';';
}

/* Für Einzeiler ohne Klammerpaar — `grab` liefe sonst bis zur nächsten
   öffnenden Klammer und zöge die folgende Deklaration mit herein. */
function line(marker) {
  const start = src.indexOf(marker);
  if (start < 0) throw new Error('nicht gefunden: ' + marker);
  const end = src.indexOf('\n', start);
  return src.slice(start, end < 0 ? src.length : end) + ';';
}

/* Nur so viel DOM, wie `sparkSvg` anfasst. */
const stub = `
  const document = {
    createElementNS: (ns, tag) => ({
      ns, tag, attrs: {}, kids: [],
      setAttribute(k, v) { this.attrs[k] = v; },
      append(c) { this.kids.push(c); },
    }),
  };
`;

const factory = new Function(stub
  + grab('const fmtRate = (b) =>')
  + grab('const fmtRateFlow = (b) =>')
  + line('const SVG_NS')
  + line('const SPARK_W')
  + grab('const sparkSvg = (vals) =>')
  + 'return { fmtRate, fmtRateFlow, sparkSvg };');

const { fmtRateFlow, sparkSvg } = factory();

let pass = 0;
const fail = [];
const check = (ok, label) => { if (ok) pass += 1; else fail.push(label); };
const eq = (got, want, label) =>
  check(got === want, `${label}: ${JSON.stringify(got)} != ${JSON.stringify(want)}`);

const lines = (svg) => svg ? svg.kids.filter((k) => k.tag === 'polyline') : [];
const dots = (svg) => svg ? svg.kids.filter((k) => k.tag === 'circle') : [];

/* --- Die eine Unterscheidung, um die es geht ------------------------- */
eq(fmtRateFlow(null), '—', 'nie gemessen');
eq(fmtRateFlow(undefined), '—', 'fehlender Wert');
eq(fmtRateFlow(0), '0', 'gemessen und still');
eq(fmtRateFlow(500), '<1K', 'unter der Anzeigeschwelle');
eq(fmtRateFlow(62903), '61K', 'echte Rate aus dem Live-Lauf (62903/1024 = 61,4)');
eq(fmtRateFlow(11708), '11K', 'zweite echte Rate');

/* --- Randfälle der Geometrie ---------------------------------------- */
eq(sparkSvg([]), null, 'leeres Array rendert nichts');
eq(sparkSvg(null), null, 'kein Array rendert nichts');
eq(sparkSvg([null, null, null]), null, 'nur Lücken rendert nichts');
eq(sparkSvg([0, 0, 0]).tag, 'svg', 'alles 0 rendert eine (flache) Linie');
check(lines(sparkSvg([0, 0, 0])).length === 1, 'alles 0: eine Linie, kein NaN');
check(!/NaN/.test(lines(sparkSvg([0, 0, 0]))[0].attrs.points), 'alles 0: keine NaN-Koordinate');

/* 19 Lücken + ein Wert — der echte Zustand eines frisch gestarteten Meters. */
const frisch = sparkSvg([null, null, null, null, null, null, null, null, null, null,
  null, null, null, null, null, null, null, null, null, 62903]);
eq(lines(frisch).length, 0, 'frischer Ring: keine Linie');
eq(dots(frisch).length, 1, 'frischer Ring: genau ein Punkt');

eq(dots(sparkSvg([1000])).length, 1, 'ein Wert ist ein Punkt, keine Linie');
eq(lines(sparkSvg([1000])).length, 0, 'ein Wert erzeugt keine Polyline');
eq(lines(sparkSvg([1000, 2000])).length, 1, 'zwei Werte: eine Linie');
eq(lines(sparkSvg([1000, 2000]))[0].attrs.points.split(' ').length, 2, 'zwei Punkte auf der Linie');
eq(lines(sparkSvg([1000, null, 2000])).length, 0, 'Lücke trennt beide Seiten');
eq(dots(sparkSvg([1000, null, 2000])).length, 2, 'Lücke: zwei Einzelpunkte');
eq(lines(sparkSvg([1, 2, null, 3, 4])).length, 2, 'Lücke teilt in zwei Segmente');

/* --- Attribute statt style --------------------------------------------- */
const svg = sparkSvg([1, 2]);
eq(svg.attrs.viewBox, '0 0 64 14', 'viewBox');
eq(svg.attrs.class, 'topo-flow-spark', 'CSS-Klasse fürs Styling');
eq(svg.attrs['preserveAspectRatio'], 'none', 'Verzerrung erlaubt');
check(!('style' in svg.attrs), 'kein style-Attribut auf dem svg');
check(!('style' in lines(svg)[0].attrs), 'kein style-Attribut auf der Linie');
check(JSON.stringify(svg).indexOf('NaN') < 0, 'nirgends NaN');

/* --- Skalierung je Zeile ---------------------------------------------- */
/* Eine gemeinsame Achse würde 700 B/s neben 62 KB/s zu einer geraden Linie
   plattdrücken — der Verlauf eines leisen Paares wäre dann nicht lesbar. */
const klein = lines(sparkSvg([1, 2]))[0].attrs.points.split(' ');
const mittel = lines(sparkSvg([1000, 2000]))[0].attrs.points.split(' ');
eq(klein[0].split(',')[1], mittel[0].split(',')[1],
  'gleiche Kurvenform bei 2 B/s und 2 KB/s (je Zeile skaliert)');
check(klein[0].split(',')[1] !== klein[1].split(',')[1],
  'ein Paar mit 2 B/s wird nicht zu einer geraden Linie plattgedrückt');
check(mittel[0].split(',')[1] !== mittel[1].split(',')[1],
  'dasselbe bei 2 KB/s');

console.log(`${pass} bestanden, ${fail.length} fehlgeschlagen`);
for (const f of fail) console.log('  - ' + f);
process.exit(fail.length ? 1 : 0);

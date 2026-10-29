/* bastian-frese.de — Boot, Terminal, Fleet, Nodes, Health, KI-Chat + Stimme */
(() => {
  'use strict';

  /* ------------------------------------------------------------------------
     Die Seite liefert `script-src 'self'` und `style-src 'self'` **ohne**
     `'unsafe-inline'`. Inline-Handler (`onclick="…"`) und Inline-Styles
     (`style="…"`) verwirft der Browser deshalb still — kein Fehler, keine
     Meldung, der Handler läuft schlicht nie. Aus dem gleichen Grund laufen
     Event-Bindings hier zentral über `on()` (nie als Attribut), Layout über
     die CSSOM (nie als style-String) und HTML über `escHtml` / `h()` (nie
     als zusammengesetzter String mit vergessener `esc()`-Stelle).
     ------------------------------------------------------------------------ */

  // `"` und `>` fehlten: ohne `"` ist jeder Attributwert eine Lücke, und
  // ohne `>` lässt sich ein Tag nicht sauber schließen. `String(s)` davor,
  // weil `s.replace` bei einer Zahl wirft — der Aufruf, der das auslöst,
  // wäre weit weg von der Ursache.
  const esc = (s) => String(s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');

  /* Tagged template: `escHtml`<b>${x}</b>`` escapt jeden Einsatz. Das ist die
     robuste Fassung von `'<b>' + esc(x) + '</b>'` — das esc() ist nicht mehr
     optional, sondern strukturell unmöglich zu vergessen. */
  const escHtml = (parts, ...vals) => {
    let out = parts[0];
    for (let i = 0; i < vals.length; i += 1) out += esc(vals[i]) + parts[i + 1];
    return out;
  };

  /* Element-Fabrik: `h('div', 'klasse', 'inhalt')`. Kürzt das immergleiche
     createElement/className/textContent-Trio und macht explizit, dass Text
     **nicht** als HTML interpretiert wird. */
  const h = (tag, cls, text) => {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  };

  /* Ein einziger Einstiegspunkt für Event-Bindings. Übernimmt den sonst
     überall wiederholten Null-Check und hält Listener aus Markup heraus, wo
     die CSP sie sowieso verwerfen würde. */
  const on = (target, event, handler, opts) => {
    if (target) target.addEventListener(event, handler, opts);
    return target;
  };

  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ---------- Scroll-Reveal ---------- */
  const revealEls = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window && !reduced) {
    const io = new IntersectionObserver((entries) => {
      entries.forEach((e) => {
        if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
      });
    }, { threshold: 0.12 });
    revealEls.forEach((el) => io.observe(el));
  } else {
    revealEls.forEach((el) => el.classList.add('in'));
  }

  /* ---------- Status-Chip (Uptime) ---------- */
  const chip = document.getElementById('status-chip');
  if (chip) {
    fetch('/api/status', { cache: 'no-store' })
      .then((r) => { if (!r.ok) throw 0; return r.json(); })
      .then((d) => {
        const s = d.uptimeSeconds || 0;
        const hrs = Math.floor(s / 3600);
        const mins = Math.floor((s % 3600) / 60);
        chip.classList.remove('loading');
        chip.textContent = 'service: active (running) · betrieb: '
          + (hrs > 0 ? hrs + ' h ' + mins + ' min' : mins + ' min');
      })
      .catch(() => {
        chip.classList.remove('loading');
        chip.classList.add('down');
        chip.textContent = 'status-check fehlgeschlagen — auch das wird hier ehrlich angezeigt';
      });
  }

  /* ---------- Metriken ---------- */
  const ttfbEl = document.getElementById('m-ttfb');
  if (ttfbEl) {
    try {
      const nav = performance.getEntriesByType('navigation')[0];
      if (nav) ttfbEl.textContent = Math.round(nav.responseStart) + ' ms';
    } catch (e) { /* Metriken sind optional */ }
  }

  /* ---------- Fleet (PVE-API): Nodes + Gäste ---------- */
  const fleetSummary = document.getElementById('fleet-summary');
  const fleetMeta = document.getElementById('fleet-meta');
  const nodeStats = document.getElementById('node-stats');
  const mFleet = document.getElementById('m-fleet');
  let lastFleet = null;

  const fmtGb = (bytes) => Math.round(bytes / (1024 * 1024 * 1024)) + ' GB';

  const pctClass = (p) => p >= 90 ? 'crit' : p >= 65 ? 'hot' : '';

  const renderNodes = (nodes) => {
    if (!nodeStats) return;
    nodeStats.textContent = '';
    nodes.forEach((n, i) => {
      const cpuPct = Math.round(n.cpu * 100);
      const memPct = n.maxMem ? Math.round(n.mem / n.maxMem * 100) : 0;
      const box = h('div', 'nbar');
      const head = h('div', 'nbar-head');
      head.append(
        h('strong', null, 'knoten ' + (i + 1)),
        h('span', null, n.status)
      );
      box.append(head);
      for (const row of [
        { lbl: 'cpu', pct: cpuPct, val: cpuPct + '%' },
        { lbl: 'ram', pct: memPct, val: fmtGb(n.mem) + ' / ' + fmtGb(n.maxMem) }
      ]) {
        const line = h('div', 'nbar-row');
        // Die Breite kommt über die CSSOM, nicht als style-Attribut: die CSP der
        // Seite ist `style-src 'self'` ohne 'unsafe-inline' und verwirft solche
        // Attribute **still** — kein Fehler, keine Meldung, der Balken bliebe
        // leer. `fill.style.width = …` ist davon ausdrücklich nicht betroffen,
        // `setAttribute('style', …)` und `.style.cssText` dagegen schon.
        const fill = h('div', 'nbar-fill ' + pctClass(row.pct));
        fill.style.width = row.pct + '%';
        const track = h('div', 'nbar-track');
        track.append(fill);
        line.append(
          h('span', 'lbl', row.lbl),
          track,
          h('span', 'val', row.val)
        );
        box.append(line);
      }
      nodeStats.append(box);
    });
  };

  const renderFleet = (data) => {
    if (!fleetMeta) return;
    lastFleet = data;
    renderChain(data);
    if (mFleet) mFleet.innerHTML = escHtml`${data.running}/${data.total} <span>aktiv</span>`;
    if (data.nodes) renderNodes(data.nodes);
    if (fleetSummary) {
      const vm = data.vm || { running: 0, total: 0 };
      const ctCount = data.ct || { running: 0, total: 0 };
      fleetSummary.textContent = 'vm ' + vm.running + '/' + vm.total
        + ' · container ' + ctCount.running + '/' + ctCount.total;
    }
    fleetMeta.innerHTML = escHtml`fleet: <span class="t-ok">${data.running}/${data.total} systeme aktiv</span> · quelle: proxmox cluster-api · live · alle 5 s`;
  };

  if (fleetMeta) {
    // Echtzeit statt Polling: Server-Sent Events. EventSource verbindet sich nach einem
    // Abbruch selbst wieder — wir müssen nur das Ereignis verarbeiten und ehrlich
    // markieren, wenn der Strom gerade unterbrochen ist.
    const fleetSrc = new EventSource('/api/fleet/stream');
    fleetSrc.onmessage = (ev) => {
      try {
        renderFleet(JSON.parse(ev.data));
      } catch (_) {
        if (fleetMeta) fleetMeta.textContent = 'fleet-daten nicht lesbar — auch das wird hier ehrlich angezeigt.';
      }
    };
    fleetSrc.onerror = () => {
      if (fleetMeta) fleetMeta.textContent = 'fleet-stream unterbrochen — verbinde neu …';
    };
  }

  /* ---------- Projekt-Health-Board ---------- */
  const healthBoard = document.getElementById('health-board');
  const renderHealth = (checks) => {
    if (!healthBoard || !Array.isArray(checks)) return;
    healthBoard.textContent = '';
    for (const c of checks) {
      const chipEl = h('span', 'health-chip ' + (c.ok ? 'ok' : 'down'));
      chipEl.innerHTML = escHtml`<span class="dot"></span>${c.name} <span class="ms">${c.ok ? c.ms + ' ms · http ' + c.status : 'down'}</span>`;
      healthBoard.append(chipEl);
    }
  };
  if (healthBoard) {
    fetch('/api/health', { cache: 'no-store' })
      .then((r) => { if (!r.ok) throw 0; return r.json(); })
      .then(renderHealth)
      .catch(() => { healthBoard.textContent = 'health-checks nicht erreichbar.'; });
  }

  /* ---------- Öffentliche Kette (ohne interne Namen) ---------- */
  // Zeigt nur, was ohnehin öffentlich erklärt ist: Internet, Cloudflare, Tunnel,
  // Proxy, Cluster. Gastnamen, Knotennamen und Zuordnungen bleiben draußen.
  const topoEl = document.getElementById('topo');
  const renderChain = (data) => {
    if (!topoEl || !data) return;

    topoEl.textContent = '';

    const chain = h('div', 'topo-chain');
    const steps = [
      { t: 'internet', s: 'besucher' },
      { t: 'cloudflare edge', s: 'dns · tls 1.3' },
      { t: 'tunnel', s: 'abgehend · 0 offene ports' },
      { t: 'nginx', s: 'security-header' },
      { t: 'cluster', s: data.running + '/' + data.total + ' aktiv' }
    ];
    steps.forEach((st, i) => {
      const pill = h('span', 'topo-pill' + (i === steps.length - 1 ? ' core' : ''));
      pill.append(h('strong', null, st.t), h('em', null, st.s));
      chain.append(pill);
      if (i < steps.length - 1) {
        const link = h('span', 'topo-link');
        link.setAttribute('aria-hidden', 'true');
        chain.append(link);
      }
    });
    topoEl.append(chain);
  };

  /* ---------- KI-Chat (Ollama) + Stimme ---------- */
  const chatState = { history: [], busy: false, voice: false };
  const chatOut = document.getElementById('chat-out');
  const chatInput = document.getElementById('chat-input');
  const voiceBtn = document.getElementById('chat-voice');

  const speak = (text) => {
    if (!chatState.voice || !('speechSynthesis' in window)) return;
    try {
      speechSynthesis.cancel();
      const u = new SpeechSynthesisUtterance(text);
      u.lang = 'de-DE';
      u.rate = 1.05;
      speechSynthesis.speak(u);
    } catch (e) { /* Stimme ist optional */ }
  };
  if (voiceBtn) {
    if (!('speechSynthesis' in window)) voiceBtn.style.display = 'none';
    on(voiceBtn, 'click', () => {
      chatState.voice = !chatState.voice;
      voiceBtn.classList.toggle('on', chatState.voice);
      voiceBtn.textContent = chatState.voice ? '🔊 stimme' : '🔇 stimme';
      if (!chatState.voice) speechSynthesis.cancel();
    });
  }

  const appendChat = (cls, who, text) => {
    if (!chatOut) return null;
    const div = h('div', 'chat-line ' + cls);
    div.append(h('span', 'who', who), h('span', 'body', text));
    chatOut.append(div);
    chatOut.scrollTop = chatOut.scrollHeight;
    return div;
  };
  const askKi = (text, history) => fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message: text, history: history.slice(-4) })
  }).then(async (r) => {
    const data = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(data.error || 'ki nicht erreichbar');
    return data.reply || '';
  });
  if (chatOut && chatInput) {
    const HELLO = 'hallo! ich laufe lokal auf einer eigenen gpu — keine cloud, keine datenabgabe. ich beantworte fragen zu bastian, seinen projekten und seinen kenntnissen — frag einfach.';
    appendChat('ai', 'ki', HELLO);
    on(chatInput, 'keydown', (ev) => {
      ev.stopPropagation();
      if (ev.key !== 'Enter' || !chatInput.value.trim()) return;
      // Ein zweites Enter während einer laufenden Anfrage darf keine zweite starten.
      // Das Flag gab es schon, es wurde nur nie gelesen — jedes Enter feuerte eine
      // weitere Anfrage und verbrauchte Kontingent, ohne dass etwas passierte.
      if (chatState.busy) return;
      chatState.busy = true;
      const text = chatInput.value.trim();
      chatInput.value = '';
      appendChat('du', 'du', text);
      const typing = appendChat('ai typing', 'ki', 'denkt nach …');
      askKi(text, chatState.history)
        .then((reply) => {
          appendChat('ai', 'ki', reply || '…');
          speak(reply);
          chatState.history.push({ role: 'user', content: text });
          chatState.history.push({ role: 'assistant', content: reply });
          chatState.history = chatState.history.slice(-4);
        })
        .catch((err) => appendChat('err', 'fehler', err.message || 'ki nicht erreichbar'))
        .finally(() => {
          chatState.busy = false;
          if (typing) typing.remove();
          chatInput.focus();
        });
    });
  }

  /* ---------- Boot-Sequenz im Hero-Terminal ---------- */
  const bootEl = document.getElementById('boot-term');
  if (bootEl) {
    const LINES = [
      { t: 'boot: portfolio.service …', c: 't-dim' },
      { t: '[  OK  ] systemd unit portfolio.service — active (running)', c: 't-ok' },
      { t: '[  OK  ] health  /health/live → "healthy"', c: 't-ok' },
      { t: '[  OK  ] fleet-endpoint an proxmox cluster-api', c: 't-ok' },
      { t: '[  OK  ] ki-chat an lokale gpu (ollama)', c: 't-ok' },
      { t: '[  OK  ] projekte 5 · 4 in echtem betrieb · 1 im aufbau', c: 't-ok' },
      { t: '[  OK  ] tests 679 automatisiert, grün', c: 't-ok' },
      { t: '[  OK  ] ki lokal · 0 cloud-calls', c: 't-ok' },
      { t: '[  OK  ] offene ports: 0', c: 't-ok' },
      { t: '', c: '' },
      { t: 'bereit. scroll für die details, oder frag das terminal unten.', c: 't-warn' }
    ];
    const render = (idx, partial) => {
      let out = '';
      for (let i = 0; i <= idx; i++) {
        const line = LINES[i];
        const text = i === idx ? partial : line.t;
        if (i > 0) out += '\n';
        out += line.c ? escHtml`<span class="${line.c}">${text}</span>` : esc(text);
      }
      return out;
    };
    const paint = (markup) => { bootEl.innerHTML = markup + '<span class="cursor">▊</span>'; };
    if (reduced) {
      bootEl.innerHTML = LINES.map((l) => l.c
        ? escHtml`<span class="${l.c}">${l.t}</span>`
        : esc(l.t)).join('\n');
    } else {
      let li = 0, ch = 0;
      const step = () => {
        if (li >= LINES.length) return;
        const line = LINES[li];
        if (ch <= line.t.length) {
          paint(render(li, line.t.slice(0, ch)));
          ch += 2;
          setTimeout(step, 14);
        } else {
          li++; ch = 0;
          setTimeout(step, li === LINES.length - 2 ? 250 : 90);
        }
      };
      step();
    }
  }

  /* ---------- Interaktives Terminal ---------- */
  const io2 = { out: document.getElementById('term-out'), in: document.getElementById('term-in-input') };
  if (io2.out && io2.in) {
    const HIST = [];
    let histIdx = -1;
    let chatMode = false;
    const write = (markup) => {
      io2.out.insertAdjacentHTML('beforeend', markup + '\n');
      io2.out.scrollTop = io2.out.scrollHeight;
    };
    const line = (text, cls) => write(cls
      ? escHtml`<span class="${cls}">${text}</span>`
      : esc(text));

    const CMDS = {
      help: () => {
        line('verfügbare befehle:', 't-dim');
        Object.keys(CMDS).sort().forEach((k) => line('  ' + k));
      },
      whoami: () => line('besucher — willkommen auf meinem betriebsgelände.'),
      status: () => {
        line('portfolio.service ................ active (running)', 't-ok');
        line('reverse proxy ................... nginx, security-header aktiv', 't-ok');
        line('transport ....................... cloudflare tunnel, 0 offene ports', 't-ok');
        line('ki-chat ......................... lokal, ollama auf eigener gpu', 't-ok');
      },
      fleet: () => {
        const d = document.getElementById('fleet-meta');
        if (d && d.textContent) line(d.textContent, 't-ok');
        else line('fleet noch nicht geladen — scroll zur fleet-sektion', 't-warn');
        if (lastFleet && lastFleet.nodes) {
          lastFleet.nodes.forEach((n, i) => {
            line('  knoten ' + (i + 1) + '  ' + Math.round(n.cpu * 100) + '% cpu · '
              + (n.maxMem ? Math.round(n.mem / n.maxMem * 100) : 0) + '% ram', 't-dim');
          });
          if (lastFleet.vm) {
            line('  vm ' + lastFleet.vm.running + '/' + lastFleet.vm.total
              + ' · container ' + lastFleet.ct.running + '/' + lastFleet.ct.total, 't-dim');
          }
        }
      },
      health: () => fetch('/api/health', { cache: 'no-store' })
        .then((r) => r.json())
        .then((checks) => {
          for (const c of checks) {
            line(c.name.padEnd(20) + (c.ok
              ? 'up   ' + c.ms + ' ms  (http ' + c.status + ')'
              : 'DOWN'), c.ok ? 't-ok' : 't-warn');
          }
        })
        .catch(() => line('health-api nicht erreichbar', 't-warn')),
      deploys: () => fetch('/api/deploys', { cache: 'no-store' })
        .then((r) => r.json())
        .then((d) => {
          line('letzte releases (neueste zuerst):', 't-dim');
          for (const r of d.releases || []) {
            line((r === d.current ? '→ ' : '  ') + r, r === d.current ? 't-ok' : '');
          }
        })
        .catch(() => line('deploy-api nicht erreichbar', 't-warn')),
      neofetch: () => {
        const info = lastFleet;
        line('        besucher@bastian-frese.de', 't-ok');
        line('        -------------------------');
        line('OS        : asp.net core mvc (net10) auf debian lxc 402');
        line('Host      : proxmox cluster "homelab" · 3 knoten', 't-ok');
        line('kernel    : cloudflare tunnel · 0 offene ports');
        line('shell     : portfolio-shell 1.0');
        if (info) {
          line('cluster   : ' + info.running + '/' + info.total + ' gäste aktiv', 't-ok');
          (info.nodes || []).forEach((n, i) => {
            line('  knoten ' + (i + 1) + '  ' + Math.round(n.cpu * 100) + '% cpu · '
              + (n.maxMem ? Math.round(n.mem / n.maxMem * 100) : 0) + '% ram');
          });
        } else {
          line('cluster   : (fleet noch nicht geladen)', 't-dim');
        }
        line('ki        : qwen3 · 1080ti · ollama · 100% lokal', 't-ok');
        line('tracking  : 0 cookies · 0 analytics · 0 externe requests', 't-ok');
      },
      chat: () => {
        chatMode = true;
        updatePrompt();
        line('chat-modus: tippe deine frage, "exit" beendet.', 't-warn');
      },
      uptime: () => fetch('/api/status', { cache: 'no-store' })
        .then((r) => r.json())
        .then((d) => {
          const s = d.uptimeSeconds || 0;
          const hrs = Math.floor(s / 3600);
          const mins = Math.floor((s % 3600) / 60);
          line('up ' + (hrs > 0 ? hrs + ' hours, ' : '') + mins + ' min — seit letztem deploy', 't-ok');
        })
        .catch(() => line('status-endpoint nicht erreichbar', 't-warn')),
      projekte: () => {
        line('erdi-erc.de .............. liga-plattform, produktiv', 't-ok');
        line('loren-flowers.shop ....... shop, abgeschlossen (Referenz online)', 't-ok');
        line('erctelemetry ............. desktop-app, beta', 't-warn');
        line('telefon-assistent ........ anrufannahme mit dialog, in betrieb', 't-ok');
        line('agent-kalender ........... android-app, im aufbau', 't-warn');
      },
      infrastruktur: () => line('3 knoten · rund 35 laufende systeme · monitoring · backups · zonen. alles oben auf der seite.'),
      ki: () => line('lokal statt cloud. betreuungs-agent + ki-chat hier im terminal (befehl: chat).'),
      stack: () => line('c#/.net · python · sql · proxmox · nginx · cloudflare tunnel · ansible · grafana/prometheus'),
      architektur: () => {
        line('der weg eines requests durch mein lab (diese seite als beispiel):', 't-dim');
        line('');
        line('  browser');
        line('    |  https');
        line('    v');
        line('  cloudflare edge ──── dns · tls 1.3 · ddos-schutz');
        line('    |');
        line('    v');
        line('  cloudflare tunnel ── abgehende verbindung · 0 offene ports');
        line('    |');
        line('    v');
        line('  nginx (reverse proxy) ── security-header · gzip');
        line('    |');
        line('    v');
        line('  portfolio.service (systemd) ── asp.net core mvc');
        line('    |');
        line('    +-- /api/fleet ──── proxmox ve cluster-api (read-only token)');
        line('    +-- /api/chat ───── ollama · qwen3:8b · eigene gpu');
        line('    +-- /api/health ─── http-checks gegen meine projekte');
        line('    +-- /api/deploys ── releases/ + current-symlink');
        line('    +-- /api/flows ──── echte traffic-raten (tap auf den knoten-bridges)');
        line('');
        line('  alles self-hosted im 3-knoten-cluster. kein datenpunkt verlässt', 't-dim');
        line('  das haus — außer über den verschlüsselten tunnel.', 't-dim');
      },
      kontakt: () => {
        line('mail: bastian@bastian-frese.de', 't-ok');
        line('(kontakt oben in der nav)', 't-dim');
      },
      ls: () => line('projekte/  infrastruktur/  fleet/  ki/  kontakt.txt'),
      exit: () => line('es gibt kein exit. das hier ist produktion. :)'),
      sudo: () => line('nice try. der versuch wurde protokolliert. (nicht wirklich — 0 cookies, 0 tracking)', 't-warn'),
      rm: () => line('rm: willst du wirklich produktion löschen? ich nicht. abgelehnt.', 't-warn'),
      clear: () => { io2.out.textContent = ''; }
    };
    CMDS.hilfe = CMDS.help;

    const updatePrompt = () => {
      const p = io2.in.closest('.term-in').querySelector('.t-prompt');
      if (p) p.textContent = chatMode ? 'du>' : 'besucher@bastian-frese.de:~$';
    };
    const run = (raw) => {
      const input = raw.trim();
      line(chatMode ? 'du> ' + input : 'besucher@bastian-frese.de:~$ ' + input, 't-cmd');
      if (!input) return;
      HIST.push(input);
      histIdx = HIST.length;
      if (chatMode) {
        if (input.toLowerCase() === 'exit' || input.toLowerCase() === 'zurueck') {
          chatMode = false;
          updatePrompt();
          line('zurück zur shell.', 't-ok');
          return;
        }
        line('ki denkt nach …', 't-dim');
        askKi(input, chatState.history)
          .then((reply) => {
            line('ki> ' + reply, 't-ok');
            chatState.history.push({ role: 'user', content: input });
            chatState.history.push({ role: 'assistant', content: reply });
            chatState.history = chatState.history.slice(-8);
          })
          .catch((e) => line('ki> ' + (e.message || 'ki nicht erreichbar'), 't-warn'));
        return;
      }
      const cmd = input.split(/\s+/)[0].toLowerCase();
      const fn = CMDS[cmd];
      if (fn) fn();
      else line('befehl nicht gefunden: ' + cmd + ' — probier help', 't-warn');
    };
    on(io2.in, 'keydown', (ev) => {
      if (ev.key === 'Enter') { run(io2.in.value); io2.in.value = ''; }
      else if (ev.key === 'ArrowUp') {
        if (histIdx > 0) { histIdx--; io2.in.value = HIST[histIdx] || ''; }
        ev.preventDefault();
      } else if (ev.key === 'ArrowDown') {
        if (histIdx < HIST.length) { histIdx++; io2.in.value = HIST[histIdx] || ''; }
        ev.preventDefault();
      }
    });
    updatePrompt();
    write('<span class="t-dim">portfolio-shell 1.0 — tippe</span> <span class="t-ok">help</span><span class="t-dim"> für befehle.</span> <span class="t-ok">neofetch</span><span class="t-dim"> und</span> <span class="t-ok">chat</span><span class="t-dim"> sind auch dabei.</span>');
  }

  /* ---------- E-Mail kopieren ---------- */
  const copyBtn = document.getElementById('copy-mail');
  on(copyBtn, 'click', () => {
    navigator.clipboard.writeText('bastian@bastian-frese.de').then(() => {
      copyBtn.textContent = 'kopiert ✓';
      setTimeout(() => { copyBtn.textContent = 'kopieren'; }, 1800);
    });
  });
})();

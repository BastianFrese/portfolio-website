/* bastian-frese.de — Boot, Terminal, Fleet, Nodes, Health, KI-Chat + Stimme */
(() => {
  'use strict';

  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

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
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        chip.classList.remove('loading');
        chip.textContent = 'service: active (running) · betrieb: '
          + (h > 0 ? h + ' h ' + m + ' min' : m + ' min');
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
  const fleetGrid = document.getElementById('fleet-grid');
  const fleetMeta = document.getElementById('fleet-meta');
  const nodeStats = document.getElementById('node-stats');
  const mFleet = document.getElementById('m-fleet');
  let lastFleet = null;

  const fmtUptime = (s) => {
    if (!s || s < 60) return s ? '< 1 min' : '—';
    const d = Math.floor(s / 86400);
    const h = Math.floor((s % 86400) / 3600);
    const m = Math.floor((s % 3600) / 60);
    if (d > 0) return d + 'd ' + h + 'h';
    if (h > 0) return h + 'h ' + m + 'min';
    return m + ' min';
  };
  const fmtGb = (bytes) => Math.round(bytes / (1024 * 1024 * 1024)) + ' GB';

  const pctClass = (p) => p >= 90 ? 'crit' : p >= 65 ? 'hot' : '';

  const renderNodes = (nodes) => {
    if (!nodeStats) return;
    nodeStats.textContent = '';
    for (const n of nodes) {
      const cpuPct = Math.round(n.cpu * 100);
      const memPct = n.maxMem ? Math.round(n.mem / n.maxMem * 100) : 0;
      const box = document.createElement('div');
      box.className = 'nbar';
      const head = document.createElement('div');
      head.className = 'nbar-head';
      head.innerHTML = '<strong>' + esc(n.name) + '</strong><span>' + esc(n.status)
        + ' · up ' + esc(fmtUptime(n.uptimeSeconds)) + '</span>';
      box.append(head);
      for (const row of [
        { lbl: 'cpu', pct: cpuPct, val: cpuPct + '%' },
        { lbl: 'ram', pct: memPct, val: fmtGb(n.mem) + ' / ' + fmtGb(n.maxMem) }
      ]) {
        const line = document.createElement('div');
        line.className = 'nbar-row';
        line.innerHTML = '<span class="lbl">' + row.lbl + '</span>'
          + '<div class="nbar-track"><div class="nbar-fill ' + pctClass(row.pct)
          + '" style="width:' + row.pct + '%"></div></div>'
          + '<span class="val">' + esc(row.val) + '</span>';
        box.append(line);
      }
      nodeStats.append(box);
    }
  };

  const renderFleet = (data) => {
    if (!fleetGrid || !fleetMeta) return;
    lastFleet = data;
    renderTopology(data);
    if (mFleet) mFleet.innerHTML = data.running + '/' + data.total + ' <span>aktiv</span>';
    if (data.nodes) renderNodes(data.nodes);
    fleetGrid.textContent = '';
    for (const g of data.guests) {
      const up = g.status === 'running';
      const tile = document.createElement('div');
      tile.className = 'tile';
      const head = document.createElement('div');
      head.className = 'tile-head';
      const dot = document.createElement('span');
      dot.className = 'dot ' + (up ? 'up' : 'down');
      const nameEl = document.createElement('span');
      nameEl.className = 'tile-name';
      nameEl.textContent = g.name;
      nameEl.title = g.name;
      const typeEl = document.createElement('span');
      typeEl.className = 'tile-type';
      typeEl.textContent = g.type;
      head.append(dot, nameEl, typeEl);
      const sub = document.createElement('div');
      sub.className = 'tile-sub';
      const nodeEl = document.createElement('span');
      nodeEl.textContent = g.node;
      const upEl = document.createElement('span');
      upEl.textContent = up ? 'up ' + fmtUptime(g.uptimeSeconds) : 'stopped';
      sub.append(nodeEl, upEl);
      tile.append(head, sub);
      fleetGrid.append(tile);
    }
    fleetMeta.innerHTML = 'fleet: <span class="t-ok">' + data.running + '/' + data.total
      + ' systeme aktiv</span> · quelle: proxmox cluster-api · cache 45 s';
  };

  const loadFleet = () => {
    if (!fleetGrid) return;
    fetch('/api/fleet', { cache: 'no-store' })
      .then((r) => { if (!r.ok) throw 0; return r.json(); })
      .then(renderFleet)
      .catch(() => {
        if (fleetMeta) fleetMeta.textContent = 'fleet-api nicht erreichbar — auch das wird hier ehrlich angezeigt.';
      });
  };
  if (fleetGrid) {
    loadFleet();
    setInterval(loadFleet, 60000);
  }

  /* ---------- Projekt-Health-Board ---------- */
  const healthBoard = document.getElementById('health-board');
  const renderHealth = (checks) => {
    if (!healthBoard || !Array.isArray(checks)) return;
    healthBoard.textContent = '';
    for (const c of checks) {
      const chipEl = document.createElement('span');
      chipEl.className = 'health-chip ' + (c.ok ? 'ok' : 'down');
      chipEl.innerHTML = '<span class="dot"></span>' + esc(c.name)
        + ' <span class="ms">' + (c.ok ? c.ms + ' ms · http ' + c.status : 'down') + '</span>';
      healthBoard.append(chipEl);
    }
  };
  if (healthBoard) {
    fetch('/api/health', { cache: 'no-store' })
      .then((r) => { if (!r.ok) throw 0; return r.json(); })
      .then(renderHealth)
      .catch(() => { healthBoard.textContent = 'health-checks nicht erreichbar.'; });
  }

  /* ---------- Live-Netzwerk-Topologie ---------- */
  const topoEl = document.getElementById('topo');
  const renderTopology = (data) => {
    if (!topoEl || !data || !data.nodes) return;

    topoEl.textContent = '';

    /* Öffentliche Kette: von außen bis in den Cluster */
    const chain = document.createElement('div');
    chain.className = 'topo-chain';
    const steps = [
      { t: 'internet', s: 'besucher' },
      { t: 'cloudflare edge', s: 'dns · tls 1.3' },
      { t: 'tunnel', s: 'abgehend · 0 offene ports' },
      { t: 'nginx', s: 'security-header' },
      { t: 'cluster', s: data.running + '/' + data.total + ' aktiv' }
    ];
    steps.forEach((st, i) => {
      const pill = document.createElement('span');
      pill.className = 'topo-pill' + (i === steps.length - 1 ? ' core' : '');
      pill.innerHTML = '<strong>' + esc(st.t) + '</strong><em>' + esc(st.s) + '</em>';
      chain.append(pill);
      if (i < steps.length - 1) {
        const link = document.createElement('span');
        link.className = 'topo-link';
        link.setAttribute('aria-hidden', 'true');
        chain.append(link);
      }
    });
    topoEl.append(chain);

    /* Backbone + Knoten-Spalten mit Gästen (live) */
    const MAX_CHIPS = 12;
    const spine = document.createElement('div');
    spine.className = 'topo-spine';
    spine.innerHTML = '<span>vmbr0 — intern · zonenkonzept: erlaubt ist, was explizit erlaubt ist</span>';
    topoEl.append(spine);

    const grid = document.createElement('div');
    grid.className = 'topo-nodes';
    for (const n of data.nodes) {
      const col = document.createElement('div');
      col.className = 'topo-node' + (n.status === 'online' ? '' : ' off');
      const guests = data.guests
        .filter((g) => g.node === n.name)
        .sort((a, b) => (a.status === b.status ? a.name.localeCompare(b.name)
          : a.status === 'running' ? -1 : 1));
      const up = guests.filter((g) => g.status === 'running').length;

      const head = document.createElement('div');
      head.className = 'topo-node-head';
      head.innerHTML = '<span class="dot ' + (n.status === 'online' ? 'up' : 'down') + '"></span>'
        + '<strong>' + esc(n.name) + '</strong>'
        + '<span class="topo-count">' + up + '/' + guests.length + '</span>';
      col.append(head);

      const list = document.createElement('div');
      list.className = 'topo-guests';
      guests.slice(0, MAX_CHIPS).forEach((g) => {
        const chip = document.createElement('span');
        chip.className = 'topo-chip' + (g.status === 'running' ? '' : ' stopped');
        chip.title = (g.type === 'lxc' ? 'container' : 'vm') + ' · ' + g.node
          + ' · ' + (g.status === 'running' ? 'up ' + fmtUptime(g.uptimeSeconds) : 'stopped');
        chip.innerHTML = '<span class="dot ' + (g.status === 'running' ? 'up' : 'down') + '"></span>'
          + '<span class="topo-chip-name">' + esc(g.name) + '</span>'
          + '<span class="topo-chip-type">' + (g.type === 'lxc' ? 'ct' : 'vm') + '</span>';
        list.append(chip);
      });
      if (guests.length > MAX_CHIPS) {
        const more = document.createElement('span');
        more.className = 'topo-more';
        more.textContent = '+' + (guests.length - MAX_CHIPS) + ' weitere';
        list.append(more);
      }
      col.append(list);
      grid.append(col);
    }
    topoEl.append(grid);
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
    voiceBtn.addEventListener('click', () => {
      chatState.voice = !chatState.voice;
      voiceBtn.classList.toggle('on', chatState.voice);
      voiceBtn.textContent = chatState.voice ? '🔊 stimme' : '🔇 stimme';
      if (!chatState.voice) speechSynthesis.cancel();
    });
  }

  const appendChat = (cls, who, text) => {
    if (!chatOut) return null;
    const div = document.createElement('div');
    div.className = 'chat-line ' + cls;
    const whoEl = document.createElement('span');
    whoEl.className = 'who';
    whoEl.textContent = who;
    const body = document.createElement('span');
    body.className = 'body';
    body.textContent = text;
    div.append(whoEl, body);
    chatOut.append(div);
    chatOut.scrollTop = chatOut.scrollHeight;
    return div;
  };
  const askKi = (text, history) => fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message: text, history: history.slice(-8) })
  }).then(async (r) => {
    const data = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(data.error || 'ki nicht erreichbar');
    return data.reply || '';
  });
  if (chatOut && chatInput) {
    const HELLO = 'hallo! ich laufe lokal auf einer eigenen gpu — keine cloud, keine datenabgabe. frag mich was über die projekte, das lab oder den weg in die systemintegration.';
    appendChat('ai', 'ki', HELLO);
    chatInput.addEventListener('keydown', (ev) => {
      ev.stopPropagation();
      if (ev.key !== 'Enter' || !chatInput.value.trim()) return;
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
          chatState.history = chatState.history.slice(-8);
        })
        .catch((err) => appendChat('err', 'fehler', err.message || 'ki nicht erreichbar'))
        .finally(() => {
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
      { t: '[  OK  ] projekte 4 live · echte nutzer · 1 beta', c: 't-ok' },
      { t: '[  OK  ] tests 340+ automatisiert, grün', c: 't-ok' },
      { t: '[  OK  ] ki lokal · 0 cloud-calls', c: 't-ok' },
      { t: '[  OK  ] offene ports: 0', c: 't-ok' },
      { t: '', c: '' },
      { t: 'bereit. scroll für die details, oder frag das terminal unten.', c: 't-warn' }
    ];
    const render = (idx, partial) => {
      let html = '';
      for (let i = 0; i <= idx; i++) {
        const line = LINES[i];
        const text = i === idx ? partial : line.t;
        if (i > 0) html += '\n';
        html += line.c ? '<span class="' + line.c + '">' + esc(text) + '</span>' : esc(text);
      }
      return html;
    };
    const paint = (html) => { bootEl.innerHTML = html + '<span class="cursor">▊</span>'; };
    if (reduced) {
      bootEl.innerHTML = LINES.map((l) => l.c ? '<span class="' + l.c + '">' + esc(l.t) + '</span>' : esc(l.t)).join('\n');
    } else {
      let li = 0, ch = 0;
      const step = () => {
        if (li >= LINES.length) return;
        const line = LINES[li];
        if (ch <= line.t.length) {
          paint(render(li, line.t.slice(0, ch)), false);
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
    const write = (html) => {
      io2.out.insertAdjacentHTML('beforeend', html + '\n');
      io2.out.scrollTop = io2.out.scrollHeight;
    };
    const line = (text, cls) => write(cls ? '<span class="' + cls + '">' + esc(text) + '</span>' : esc(text));

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
          for (const n of lastFleet.nodes) {
            line('  ' + n.name.padEnd(8) + Math.round(n.cpu * 100) + '% cpu · '
              + (n.maxMem ? Math.round(n.mem / n.maxMem * 100) : 0) + '% ram · up '
              + fmtUptime(n.uptimeSeconds), 't-dim');
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
          for (const n of info.nodes || []) {
            line('  ' + n.name.padEnd(8) + Math.round(n.cpu * 100) + '% cpu · '
              + (n.maxMem ? Math.round(n.mem / n.maxMem * 100) : 0) + '% ram · up ' + fmtUptime(n.uptimeSeconds));
          }
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
          const h = Math.floor(s / 3600);
          const m = Math.floor((s % 3600) / 60);
          line('up ' + (h > 0 ? h + ' hours, ' : '') + m + ' min — seit letztem deploy', 't-ok');
        })
        .catch(() => line('status-endpoint nicht erreichbar', 't-warn')),
      projekte: () => {
        line('erdi-erc.de .............. liga-plattform, produktiv', 't-ok');
        line('loren-flowers.shop ....... shop, abgeschlossen (Referenz online)', 't-ok');
        line('erctelemetry ............. desktop-app, beta', 't-warn');
        line('lyra ..................... lokaler ki-assistent', 't-ok');
      },
      infrastruktur: () => line('3 knoten · rund 30 systeme · monitoring · backups · zonen. alles oben auf der seite.'),
      ki: () => line('lokal statt cloud. sprachassistent + betreuungs-agent + ki-chat hier im terminal (befehl: chat).'),
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
    io2.in.addEventListener('keydown', (ev) => {
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
  if (copyBtn) {
    copyBtn.addEventListener('click', () => {
      navigator.clipboard.writeText('bastian@bastian-frese.de').then(() => {
        copyBtn.textContent = 'kopiert ✓';
        setTimeout(() => { copyBtn.textContent = 'kopieren'; }, 1800);
      });
    });
  }
})();
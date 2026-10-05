/* ============================================================
   bastian-frese.de — Effekt-Engine
   Läuft nach site.js. Jeder Block prüft sein Ziel selbst und
   steigt sonst still aus: fehlt Markup, fehlt der Effekt — aber
   die Seite bleibt in jedem Fall lesbar.

   CSP: kein style-Attribut, nur CSSOM (el.style.* bzw.
   style.setProperty). style-Attribute verwirft die CSP still.
   ============================================================ */
(() => {
  'use strict';

  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const ESC = (s) => String(s).replace(/[&<>"]/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]
  ));

  /* ---------- Scroll-Fortschritt ---------- */
  (() => {
    const bar = document.getElementById('fx-progress');
    if (!bar) return;
    let queued = false;
    const paint = () => {
      queued = false;
      const max = document.documentElement.scrollHeight - window.innerHeight;
      const p = max > 0 ? window.scrollY / max : 0;
      bar.style.transform = 'scaleX(' + Math.min(1, Math.max(0, p)).toFixed(4) + ')';
    };
    addEventListener('scroll', () => { if (!queued) { queued = true; requestAnimationFrame(paint); } }, { passive: true });
    paint();
  })();

  /* ---------- Weiches Scrollen ----------
     Lenis übernimmt das Rad und die Sprungmarken. Fehlt das Skript oder
     ist Bewegung reduziert, bleibt der native Scroll — die Seite
     funktioniert in beiden Fällen, ohne Sonderfall im Markup. */
  (() => {
    if (reduced || typeof window.Lenis !== 'function') return;

    /* Innere Scroll-Bereiche dürfen Lenis nicht an den Seiten-Scroll ketten. */
    document.querySelectorAll('.term-out, .chat-out').forEach((el) => {
      el.setAttribute('data-lenis-prevent', '');
    });

    new window.Lenis({ anchors: true, autoRaf: true, lerp: .1 });
  })();

  /* ---------- Boot-Overlay ----------
     Wird aus JS gebaut, nicht aus dem Markup: scheitert das Skript,
     erscheint gar kein Overlay statt eines, das den Inhalt verdeckt. */
  (() => {
    if (reduced) return;
    let shown = false;
    try { shown = sessionStorage.getItem('fx-boot') === '1'; } catch (_) { shown = false; }
    if (shown) return;

    const LINES = [
      ['netzwerk-schnittstelle', 'up'],
      ['zertifikat geprüft', 'ok'],
      ['portfolio.service — active (running)', 'ok'],
      ['fleet-anbindung an proxmox cluster-api', 'ok'],
      ['ki lokal · 0 cloud-calls', 'ok']
    ];

    const ov = document.createElement('div');
    ov.id = 'boot-overlay';
    ov.setAttribute('role', 'status');
    ov.innerHTML =
      '<div class="boot-inner">' +
        '<div class="b-head">bastian-frese.de — systemstart</div>' +
        LINES.map((l) =>
          '<div class="ln">' +
            '<span class="dim">[ </span><span class="' + l[1] + '">OK</span><span class="dim"> ]</span> ' + ESC(l[0]) +
          '</div>').join('') +
        '<div class="boot-bar"><i></i></div>' +
        '<div class="boot-skip">klicken oder taste drücken, um zu überspringen</div>' +
      '</div>';

    /* Versatz der Zeilen über CSSOM — nicht über ein style-Attribut. */
    ov.querySelectorAll('.ln').forEach((el, i) => {
      el.style.animationDelay = (i * 190) + 'ms';
    });

    document.body.appendChild(ov);

    let done = false;
    const close = () => {
      if (done) return;
      done = true;
      ov.classList.add('gone');
      try { sessionStorage.setItem('fx-boot', '1'); } catch (_) { /* privat modus */ }
      setTimeout(() => ov.remove(), 600);
      removeEventListener('keydown', close);
      removeEventListener('pointerdown', close);
    };
    addEventListener('keydown', close);
    addEventListener('pointerdown', close);
    setTimeout(close, 2300);
    /* Notausstieg: egal was passiert, das Overlay verschwindet. */
    setTimeout(close, 6000);
  })();

  /* ---------- Lebendiges Netzwerk im Hero ---------- */
  (() => {
    const cv = document.getElementById('hero-net');
    if (!cv || reduced || !cv.getContext) return;
    const ctx = cv.getContext('2d');
    if (!ctx) return;

    const LINK_DIST = 132;
    const MOUSE_DIST = 170;
    const mouse = { x: -9999, y: -9999 };
    let nodes = [], packets = [], w = 0, h = 0, dpr = 1, running = true, raf = 0;

    const size = () => {
      const r = cv.getBoundingClientRect();
      if (!r.width || !r.height) return false;
      dpr = Math.min(2, window.devicePixelRatio || 1);
      w = r.width; h = r.height;
      cv.width = Math.round(w * dpr);
      cv.height = Math.round(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      return true;
    };

    const spawn = () => {
      const n = Math.round((w * h) / 16000);
      nodes = Array.from({ length: Math.max(22, Math.min(80, n)) }, () => ({
        x: Math.random() * w,
        y: Math.random() * h,
        vx: (Math.random() - .5) * .28,
        vy: (Math.random() - .5) * .28,
        r: Math.random() * 1.5 + 1
      }));
      packets = [];
    };

    const step = () => {
      ctx.clearRect(0, 0, w, h);

      for (const p of nodes) {
        p.x += p.vx; p.y += p.vy;
        if (p.x < 0 || p.x > w) p.vx *= -1;
        if (p.y < 0 || p.y > h) p.vy *= -1;
        p.x = Math.max(0, Math.min(w, p.x));
        p.y = Math.max(0, Math.min(h, p.y));
      }

      /* Linien zwischen nahen Knoten; Nähe zum Zeiger hellt auf. */
      for (let i = 0; i < nodes.length; i++) {
        const a = nodes[i];
        for (let j = i + 1; j < nodes.length; j++) {
          const b = nodes[j];
          const dx = a.x - b.x, dy = a.y - b.y;
          const d = Math.hypot(dx, dy);
          if (d > LINK_DIST) continue;
          const near = Math.hypot((a.x + b.x) / 2 - mouse.x, (a.y + b.y) / 2 - mouse.y);
          const hot = near < MOUSE_DIST ? (1 - near / MOUSE_DIST) : 0;
          const base = (1 - d / LINK_DIST) * .34;
          ctx.strokeStyle = 'rgba(' + (hot > 0 ? '255,138,61' : '127,168,201') + ',' + (base + hot * .45).toFixed(3) + ')';
          ctx.lineWidth = 1;
          ctx.beginPath();
          ctx.moveTo(a.x, a.y);
          ctx.lineTo(b.x, b.y);
          ctx.stroke();

          /* Auf einer heißen Kante gelegentlich ein Paket losschicken. */
          if (hot > .55 && packets.length < 5 && Math.random() < .004) {
            packets.push({ a, b, t: 0, sp: .012 + Math.random() * .02 });
          }
        }
      }

      for (const p of nodes) {
        const near = Math.hypot(p.x - mouse.x, p.y - mouse.y);
        const hot = near < MOUSE_DIST ? (1 - near / MOUSE_DIST) : 0;
        ctx.fillStyle = hot > 0
          ? 'rgba(255,138,61,' + (.5 + hot * .5).toFixed(2) + ')'
          : 'rgba(127,168,201,.55)';
        ctx.beginPath();
        ctx.arc(p.x, p.y, p.r + hot * 1.6, 0, Math.PI * 2);
        ctx.fill();
      }

      packets = packets.filter((pk) => {
        pk.t += pk.sp;
        if (pk.t >= 1) return false;
        const x = pk.a.x + (pk.b.x - pk.a.x) * pk.t;
        const y = pk.a.y + (pk.b.y - pk.a.y) * pk.t;
        ctx.fillStyle = '#ff8a3d';
        ctx.beginPath();
        ctx.arc(x, y, 2.1, 0, Math.PI * 2);
        ctx.fill();
        ctx.strokeStyle = 'rgba(255,138,61,.35)';
        ctx.lineWidth = 1.4;
        ctx.beginPath();
        ctx.moveTo(pk.a.x + (pk.b.x - pk.a.x) * Math.max(0, pk.t - .06), pk.a.y + (pk.b.y - pk.a.y) * Math.max(0, pk.t - .06));
        ctx.lineTo(x, y);
        ctx.stroke();
        return true;
      });

      if (running) raf = requestAnimationFrame(step);
    };

    const start = () => {
      if (!size()) return;
      spawn();
      if (!raf) raf = requestAnimationFrame(step);
    };

    cv.parentElement.addEventListener('pointermove', (e) => {
      const r = cv.getBoundingClientRect();
      mouse.x = e.clientX - r.left;
      mouse.y = e.clientY - r.top;
    });
    cv.parentElement.addEventListener('pointerleave', () => { mouse.x = mouse.y = -9999; });

    let rt = 0;
    addEventListener('resize', () => { clearTimeout(rt); rt = setTimeout(() => { size(); spawn(); }, 200); });

    /* Nur rechnen, wenn der Hero sichtbar ist und der Tab aktiv. */
    document.addEventListener('visibilitychange', () => {
      running = !document.hidden;
      if (running && !raf) raf = requestAnimationFrame(step);
      if (!running && raf) { cancelAnimationFrame(raf); raf = 0; }
    });
    if ('IntersectionObserver' in window) {
      new IntersectionObserver((es) => {
        for (const e of es) {
          running = e.isIntersecting && !document.hidden;
          if (running && !raf) raf = requestAnimationFrame(step);
          if (!running && raf) { cancelAnimationFrame(raf); raf = 0; }
        }
      }, { threshold: 0 }).observe(cv);
    }

    start();
  })();

  /* ---------- Zahlen zählen hoch ---------- */
  (() => {
    const els = document.querySelectorAll('[data-count]');
    if (!els.length) return;
    if (reduced || !('IntersectionObserver' in window)) return;

    const fmt = new Intl.NumberFormat('de-DE');
    const run = (el) => {
      const m = /^(\s*)(\d[\d.]*)([\s\S]*)$/.exec(el.textContent);
      if (!m) return;
      const target = parseInt(m[2].replace(/\./g, ''), 10);
      if (!isFinite(target) || target <= 0) return;
      const pre = m[1], post = m[3];
      const t0 = performance.now(), dur = 900;
      const tick = (now) => {
        const k = Math.min(1, (now - t0) / dur);
        const eased = 1 - Math.pow(1 - k, 3);
        el.textContent = pre + fmt.format(Math.round(target * eased)) + post;
        if (k < 1) requestAnimationFrame(tick);
        else el.classList.add('fx-counted');
      };
      el.textContent = pre + fmt.format(0) + post;
      requestAnimationFrame(tick);
    };

    const io = new IntersectionObserver((es, obs) => {
      for (const e of es) if (e.isIntersecting) { run(e.target); obs.unobserve(e.target); }
    }, { threshold: .5 });
    els.forEach((el) => io.observe(el));
  })();

  /* ---------- Diagramme zeichnen sich ---------- */
  (() => {
    const svgs = document.querySelectorAll('svg[data-draw]');
    if (!svgs.length || reduced || !('IntersectionObserver' in window)) return;

    const arm = (svg) => {
      svg.querySelectorAll('path, line, rect, polyline').forEach((el) => {
        let len = 400;
        try {
          if (typeof el.getTotalLength === 'function') {
            len = Math.max(1, Math.ceil(el.getTotalLength()));
          } else if (el.tagName.toLowerCase() === 'rect') {
            const vb = svg.viewBox.baseVal;
            const w = el.width.baseVal.value || vb.width;
            const h = el.height.baseVal.value || vb.height;
            len = Math.ceil(2 * (w + h));
          }
        } catch (_) { len = 400; }
        el.style.setProperty('--len', String(len));
      });
      svg.classList.add('fx-draw');
    };

    const io = new IntersectionObserver((es, obs) => {
      for (const e of es) if (e.isIntersecting) { arm(e.target); obs.unobserve(e.target); }
    }, { threshold: .25 });
    svgs.forEach((s) => io.observe(s));
  })();

  /* ---------- Karten: Spotlight + Kippung ---------- */
  (() => {
    if (reduced) return;
    if (window.matchMedia('(hover: none)').matches) return; /* Touch: kein Kippen */

    document.querySelectorAll('.card').forEach((card) => {
      let raf = 0, tx = 0, ty = 0;

      const apply = () => {
        raf = 0;
        card.style.transform =
          'perspective(900px) rotateX(' + (-ty * 5).toFixed(2) + 'deg) rotateY(' + (tx * 5).toFixed(2) + 'deg) translateY(-2px)';
      };
      const move = (e) => {
        const r = card.getBoundingClientRect();
        const px = (e.clientX - r.left) / r.width;
        const py = (e.clientY - r.top) / r.height;
        card.classList.add('fx-live');
        card.style.setProperty('--mx', (px * 100).toFixed(1) + '%');
        card.style.setProperty('--my', (py * 100).toFixed(1) + '%');
        tx = px * 2 - 1;
        ty = py * 2 - 1;
        if (!raf) raf = requestAnimationFrame(apply);
      };
      const leave = () => {
        card.classList.remove('fx-live');
        card.style.transform = '';
      };

      card.addEventListener('pointermove', move, { passive: true });
      card.addEventListener('pointerleave', leave);
    });
  })();

  /* ---------- Staffelung + Sweep ---------- */
  (() => {
    if (!('IntersectionObserver' in window)) return;

    /* Innerhalb jeder Gruppe versetzt einblenden. */
    document.querySelectorAll('.grid, .chip-groups, .fleet-grid, .metrics, .pipe').forEach((group) => {
      const kids = group.querySelectorAll(':scope > .reveal');
      kids.forEach((el, i) => {
        el.style.setProperty('--fx-delay', (reduced ? 0 : i * 70) + 'ms');
      });
    });

    if (reduced) return;
    const io = new IntersectionObserver((es, obs) => {
      for (const e of es) {
        if (!e.isIntersecting) continue;
        e.target.classList.add('fx-sweep');
        obs.unobserve(e.target);
      }
    }, { threshold: .12 });
    document.querySelectorAll('.section').forEach((s) => io.observe(s));
  })();
})();

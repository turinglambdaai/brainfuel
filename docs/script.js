/**
 * BrainFuel — interactions, i18n, and live release links.
 */

document.addEventListener('DOMContentLoaded', () => {
    initSmoothScrolling();
    initScrollAnimations();
    initLanguageSwitcher();
    initReleaseLinks();
});

// Smooth scrolling for in-page anchors.
function initSmoothScrolling() {
    document.querySelectorAll('a[href^="#"]').forEach(anchor => {
        anchor.addEventListener('click', function (e) {
            const targetId = this.getAttribute('href');
            if (targetId === '#' || !targetId || targetId === '#lang-toggle') return;

            const targetElement = document.querySelector(targetId);
            if (targetElement) {
                e.preventDefault();
                const headerOffset = 64;
                const elementPosition = targetElement.getBoundingClientRect().top;
                const offsetPosition = elementPosition + window.pageYOffset - headerOffset;

                window.scrollTo({
                    top: offsetPosition,
                    behavior: 'smooth'
                });
            }
        });
    });
}

// Reveal elements as they scroll into view.
function initScrollAnimations() {
    const observer = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                entry.target.classList.add('is-visible');
                observer.unobserve(entry.target);
            }
        });
    }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });

    document.querySelectorAll('.animate-on-scroll').forEach(el => observer.observe(el));
}

// Language policy:
// 1. A user's explicit choice always wins and is persisted.
// 2. With no explicit choice, follow the browser/system locale on every visit.
// 3. Chinese locales use Simplified Chinese; other known locales use English.
// 4. If a locale cannot be detected at all, fall back to Chinese because the
//    current primary audience is Chinese-speaking.
//
// The v2 key intentionally does not reuse the legacy `brainfuel_lang` key.
// Older versions wrote the automatically detected language into that key on
// first visit, so it cannot distinguish an explicit choice from auto-detection.
const LANGUAGE_OVERRIDE_KEY = 'brainfuel_lang_override_v2';
const LEGACY_LANGUAGE_KEY = 'brainfuel_lang';

const translations = {
    en: 'English',
    zh: '简体中文'
};

const pageMetadata = {
    en: {
        title: 'BrainFuel — GLM Coding Plan quota monitor',
        description: 'A tiny always-on-top desktop widget that monitors your GLM Coding Plan quota — the 5-hour rolling window and the weekly allowance. Cross-platform.'
    },
    zh: {
        title: 'BrainFuel — GLM Coding Plan 配额监控器',
        description: '一个轻量、始终置顶的跨平台桌面小部件，用于监控 GLM Coding Plan 的 5 小时滚动窗口与每周配额。'
    }
};

function readLanguageOverride() {
    try {
        const saved = localStorage.getItem(LANGUAGE_OVERRIDE_KEY);
        return saved === 'zh' || saved === 'en' ? saved : null;
    } catch (_) {
        return null;
    }
}

function detectBrowserLanguage() {
    const locale = (navigator.languages && navigator.languages[0]) || navigator.language || '';
    if (!locale) return 'zh';
    return String(locale).toLowerCase().startsWith('zh') ? 'zh' : 'en';
}

function initLanguageSwitcher() {
    try { localStorage.removeItem(LEGACY_LANGUAGE_KEY); } catch (_) { /* private mode */ }

    const override = readLanguageOverride();
    applyLanguage(override || detectBrowserLanguage(), false);

    window.addEventListener('languagechange', () => {
        if (!readLanguageOverride()) {
            applyLanguage(detectBrowserLanguage(), false);
        }
    });
}

window.setLanguage = function (lang) {
    if (lang !== 'zh' && lang !== 'en') return;
    applyLanguage(lang, true);
};

function applyLanguage(lang, persist) {
    if (persist) {
        try { localStorage.setItem(LANGUAGE_OVERRIDE_KEY, lang); } catch (_) { /* private mode */ }
    }

    document.documentElement.lang = lang === 'zh' ? 'zh-CN' : 'en';
    document.documentElement.dataset.language = lang;

    // Update translatable text.
    // - Leaf elements (no element children): set innerText directly.
    // - Elements with inline markup in their data-* attribute (e.g. <code>, <strong>):
    //   the attribute is author-controlled and safe to render as innerHTML, so we use
    //   that path to support mixed text+markup like "Lowercase <code>m</code> = minutes".
    document.querySelectorAll('[data-en], [data-zh]').forEach(el => {
        const text = el.getAttribute('data-' + lang);
        if (!text) return;

        const hasElementChildren = Array.from(el.children).some(c => c.nodeType === 1);
        if (!hasElementChildren) {
            el.innerText = text;
        } else if (/<\w/.test(text)) {
            el.innerHTML = text;
        }
    });

    const currentLangLabel = document.getElementById('current-lang-label');
    if (currentLangLabel && translations[lang]) {
        currentLangLabel.innerText = translations[lang];
    }

    const metadata = pageMetadata[lang];
    if (metadata) {
        document.title = metadata.title;
        const description = document.querySelector('meta[name="description"]');
        if (description) description.setAttribute('content', metadata.description);
    }
}

// Fetch the latest GitHub release and point each platform card at the right asset.
// Falls back gracefully to the releases page if the API is unreachable.
async function initReleaseLinks() {
    const assetMatchers = {
        'dl-macos': name => /\.dmg$/i.test(name),
        'dl-windows': name => /setup\.exe$/i.test(name),
        'dl-linux': name => /\.appimage$/i.test(name),
    };

    const versionBadge = document.getElementById('ver-badge');

    try {
        const res = await fetch('https://api.github.com/repos/turinglambdaai/brainfuel/releases/latest', {
            headers: { 'Accept': 'application/vnd.github+json' }
        });
        if (!res.ok) return;

        const release = await res.json();

        // Version badge.
        if (versionBadge && release.tag_name) {
            versionBadge.textContent = release.tag_name;
        }

        // Resolve each platform's preferred asset.
        for (const [id, match] of Object.entries(assetMatchers)) {
            const card = document.getElementById(id);
            if (!card) continue;

            const asset = (release.assets || []).find(a => match(a.name));
            if (asset && asset.browser_download_url) {
                card.href = asset.browser_download_url;
            }
        }
    } catch (err) {
        // Network/CORS/offline — keep the fallback href (releases page).
        console.warn('BrainFuel: could not fetch latest release, using fallback link.');
    }
}

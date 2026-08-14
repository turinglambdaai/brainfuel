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

// Language switching (persisted to localStorage).
const translations = {
    en: 'English',
    zh: '简体中文'
};

function initLanguageSwitcher() {
    const savedLang = localStorage.getItem('brainfuel_lang');
    const browserLang = navigator.language.startsWith('zh') ? 'zh' : 'en';
    setLanguage(savedLang || browserLang);
}

window.setLanguage = function (lang) {
    localStorage.setItem('brainfuel_lang', lang);
    document.documentElement.lang = lang;

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
};

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
        // Network/CORS/ offline — keep the fallback href (releases page).
        console.warn('BrainFuel: could not fetch latest release, using fallback link.');
    }
}

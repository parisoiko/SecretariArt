// Behaviour shared by every page, MVC and Blazor alike. It's loaded once per full page load: Blazor's enhanced
// navigation swaps the page content without reloading scripts, so everything here listens on the document, which
// stays the same.

// Forms marked data-confirm (deletes, removals) ask first; the text names what will go.
document.addEventListener('submit', e => {
    const message = e.target.dataset?.confirm;
    if (message && !confirm(message)) {
        e.preventDefault();
        e.stopImmediatePropagation();
    }
}, true);

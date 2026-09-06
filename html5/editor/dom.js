// -----------------------------------------------------------------------------
// dom — the handful of element helpers the panels share.
//
// Deliberately not a framework. The editor's panels rebuild small subtrees on
// change, which is fast enough for a hierarchy of a few hundred actors and
// leaves nothing between the code and the DOM to reason about.
// -----------------------------------------------------------------------------

/**
 * Builds an element.
 *
 * @param {string} tag Optionally with classes: `'div.panel.is-open'`.
 * @param {object} [props] Attributes, `style`, `dataset`, and `on*` handlers.
 * @param {...(Node|string|null)} children
 */
export function el(tag, props = {}, ...children) {
    const [name, ...classes] = tag.split('.');
    const node = document.createElement(name || 'div');
    if (classes.length) node.className = classes.join(' ');

    for (const [key, value] of Object.entries(props ?? {})) {
        if (value == null) continue;

        if (key === 'class') node.className = `${node.className} ${value}`.trim();
        else if (key === 'style') Object.assign(node.style, value);
        else if (key === 'dataset') Object.assign(node.dataset, value);
        else if (key === 'text') node.textContent = value;
        else if (key.startsWith('on') && typeof value === 'function') {
            node.addEventListener(key.slice(2).toLowerCase(), value);
        } else if (key in node && key !== 'list') {
            node[key] = value;
        } else {
            node.setAttribute(key, value);
        }
    }

    for (const child of children.flat()) {
        if (child == null || child === false) continue;
        node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }

    return node;
}

/** Removes every child of an element. */
export function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
    return node;
}

/** A labelled row, the shape every inspector field takes. */
export function field(label, ...controls) {
    return el('label.sb-field', {},
        el('span.sb-field-label', { text: label, title: label }),
        el('div.sb-field-controls', {}, ...controls));
}

/** A number input that reports changes as it is typed. */
export function numberInput(value, onChange, { step = 0.1, min = null, max = null } = {}) {
    const input = el('input.sb-number', {
        type: 'number',
        value: formatNumber(value),
        step,
        oninput: () => {
            const parsed = parseFloat(input.value);
            if (Number.isFinite(parsed)) onChange(parsed);
        },
    });
    if (min != null) input.min = min;
    if (max != null) input.max = max;
    return input;
}

/** Trims floating-point noise so a field shows 0.25 rather than 0.25000000004. */
export function formatNumber(value) {
    if (!Number.isFinite(value)) return '0';
    return String(Math.round(value * 1e4) / 1e4);
}

/** Escapes text for use in a title attribute or a label. */
export function truncate(text, length = 40) {
    const value = String(text ?? '');
    return value.length <= length ? value : `${value.slice(0, length - 1)}…`;
}

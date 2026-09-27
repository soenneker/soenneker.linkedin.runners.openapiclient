() => {
    const normalize = value => value.replace(/\r/g, '').replace(/[ \t]+/g, ' ').trim();
    const roots = [...document.querySelectorAll('main .content')];
    const title = document.querySelector('main h1')?.textContent?.trim() || '';
    const blocks = [];
    const links = new Set();
    let section = title;
    let anchor = document.querySelector('main h1')?.id || '';
    for (const root of roots) {
        for (const a of root.querySelectorAll('a[href]')) links.add(a.href);
        for (const element of root.querySelectorAll('h2,h3,h4,h5,table,pre,p,ul,ol')) {
            // Tables/lists/pre are captured as a unit, including text in nested cells.
            if (element.parentElement.closest('table,pre,ul,ol')) continue;
            if (/^H[2-5]$/.test(element.tagName)) {
                section = normalize(element.textContent);
                anchor = element.id;
                continue;
            }
            const block = { Kind: 'text', Section: section, Anchor: anchor, Text: normalize(element.textContent), Headers: [], Rows: [] };
            if (element.tagName === 'TABLE') {
                block.Kind = 'table';
                const rows = [...element.rows];
                block.Headers = [...(rows.shift()?.cells || [])].map(c => normalize(c.textContent));
                block.Rows = rows.map(row => [...row.cells].map(cell => ({
                    Text: normalize(cell.textContent),
                    Links: [...cell.querySelectorAll('a[href]')].map(a => a.href),
                    Values: [...cell.querySelectorAll('li')].map(li => normalize(li.textContent))
                })));
                block.Text = '';
            } else if (element.tagName === 'PRE') {
                block.Kind = 'code';
                block.Text = element.textContent.trim();
            }
            if (block.Text || block.Rows.length) blocks.push(block);
        }
    }
    // Include the rendered documentation navigation to discover newly added articles.
    for (const a of document.querySelectorAll('#ms--toc-content a[href], nav[aria-label="Table of contents"] a[href]')) links.add(a.href);
    const requested = new URL(location.href).searchParams.get('view');
    const available = [...document.querySelectorAll('meta[name="monikers"]')].map(m => m.content);
    if (requested && available.length && !available.includes(requested))
        throw new Error('Requested documentation version is not available: ' + requested);
    return {
        Url: location.href, Title: title,
        Version: requested || document.querySelector('meta[name="default_moniker"]')?.content || '',
        Links: [...links].sort(), Blocks: blocks
    };
}

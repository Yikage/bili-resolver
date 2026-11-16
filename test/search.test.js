/**
 * VRChat 世界内搜索：/search 存结果到 D1 slot，/p/{slot}/{i} 读取后 302 播放。
 * D1 用 node:sqlite 内存库模拟（prepare().bind().first()/run() 子集）。
 */

import { describe, it, expect, afterEach } from 'vitest';
import { createRequire } from 'node:module';
import worker, { resetTransportHealth } from '../index.js';
import { installFetchMock, makeResponse } from './setup.js';

afterEach(() => resetTransportHealth());

// vitest 2 的模块解析不认识 node:sqlite，改用 require 加载
const { DatabaseSync } = createRequire(import.meta.url)('node:sqlite');

const HOST = 'https://bili.test';
const IMG_URL = 'https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png';
const SUB_URL = 'https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png';

function makeD1() {
    const sqlite = new DatabaseSync(':memory:');
    sqlite.exec('CREATE TABLE search_slots (slot INTEGER PRIMARY KEY, query TEXT NOT NULL, results TEXT NOT NULL, created_at INTEGER NOT NULL)');
    return {
        _sqlite: sqlite,
        prepare(sql) {
            let args = [];
            const stmt = {
                bind(...a) { args = a; return stmt; },
                async first() { return sqlite.prepare(sql).get(...args) ?? null; },
                async run() { sqlite.prepare(sql).run(...args); return { success: true }; },
            };
            return stmt;
        },
    };
}

function searchResult(n) {
    return Array.from({ length: n }, (_, i) => ({
        bvid: `BV1xx411c7m${String.fromCharCode(65 + i)}`,
        title: `<em class="keyword">原神</em> 第${i}期 &amp; more`,
        author: `UP${i}`,
        duration: '4:30',
    }));
}

function installBiliMock(searchCalls) {
    return installFetchMock({
        'finger/spi': () => makeResponse({ code: 0, data: { b_3: 'B3', b_4: 'B4' } }),
        'GenWebTicket': () => makeResponse({ code: 0, data: { ticket: 'T' } }),
        'web-interface/nav': () => makeResponse({ code: 0, data: { wbi_img: { img_url: IMG_URL, sub_url: SUB_URL } } }),
        'search/type': (url) => { searchCalls.push(url); return makeResponse({ code: 0, data: { result: searchResult(12) } }); },
        'web-interface/view': () => makeResponse({ code: 0, data: { cid: 123, title: 'T', pic: '', owner: { name: 'A' } } }),
        'playurl': () => makeResponse({ code: 0, data: { quality: 80, durl: [{ url: 'https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/x.mp4?deadline=9999999999' }] } }),
    });
}

describe('/search + /p/{slot}/{i}', () => {
    it('stores cleaned results in a slot, then /p plays the chosen result for any viewer', async () => {
        const env = { SEARCH_DB: makeD1() };
        const searchCalls = [];
        installBiliMock(searchCalls);

        const res = await worker.fetch(new Request(`${HOST}/search?q=${encodeURIComponent('原神')}`), env, { waitUntil() {} });
        const body = await res.json();
        expect(body.status).toBe('success');
        expect(body.slot).toBe(0);
        expect(body.perSlot).toBe(10);
        expect(body.results).toHaveLength(10);
        expect(body.results[0].title).toBe('原神 第0期 & more');
        expect(searchCalls[0]).toContain('wbi/search/type');
        expect(searchCalls[0]).toContain('w_rid=');

        const play = await worker.fetch(new Request(`${HOST}/p/0/3`), env, { waitUntil() {} });
        expect(play.status).toBe(302);
        expect(play.headers.get('Location')).toContain('/proxy?url=');
    });

    it('reuses the slot for a repeated query and rotates to new slots for new queries', async () => {
        const env = { SEARCH_DB: makeD1() };
        const searchCalls = [];
        installBiliMock(searchCalls);
        const search = async (q) => (await worker.fetch(new Request(`${HOST}/search?q=${encodeURIComponent(q)}`), env, {})).json();

        expect((await search('a')).slot).toBe(0);
        expect((await search('b')).slot).toBe(1);
        expect((await search('a')).slot).toBe(0);
        expect(searchCalls).toHaveLength(2);
    });

    it('returns errors instead of crashing for bad input', async () => {
        const env = { SEARCH_DB: makeD1() };
        installBiliMock([]);
        expect((await (await worker.fetch(new Request(`${HOST}/search?q=`), env, {})).json()).status).toBe('error');
        expect((await (await worker.fetch(new Request(`${HOST}/search?q=x`), {}, {})).json()).message).toContain('SEARCH_DB');
        expect((await worker.fetch(new Request(`${HOST}/p/5/0`), env, {})).status).toBe(404);
        expect((await worker.fetch(new Request(`${HOST}/p/0/99`), env, {})).status).toBe(404);
    });
});

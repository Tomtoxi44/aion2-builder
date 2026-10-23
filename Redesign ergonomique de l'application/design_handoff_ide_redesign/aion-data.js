// Données fictives + calcul des vues, partagés par les prototypes Néon et IDE.
export const GRADES = { Common:['Commun (blanc)','#EDEDED'], Rare:['Rare (vert)','#52B35C'], Legend:['Épique (bleu)','#3D94D8'], Unique:['Unique (jaune)','#FB9800'] };
export const STATUS = { Empty:['Vide','#E5484D','E'], Replace:['À remplacer','#F5A524','W'], Weak:['En retard','#F5A524','W'], Enchant:['À enchanter','#3FA7D6','I'], Ok:['OK','#46A758','✓'] };
const GOOD = '#46A758', WARN = '#F5A524', BAD = '#E5484D', INFO = '#3FA7D6';
const SRC_Y = 'Armures jaunes d\'expédition ; commence par les pièces les plus faibles.';
const SRC_A = 'Accessoires jaunes d\'expédition / transmutation.';
const SLOTS = [
  ['Arme','7 / 2','Épée longue du gardien','Unique',1620,6,'Enchant','Pièce à garder : monte-la à +8, c\'est la pièce qui pèse le plus lourd.','Arme jaune vers +8 (enchantement en priorité).'],
  ['Main secondaire','7 / 3','Bouclier de la garnison','Legend',1310,3,'Replace','Pièce bleue de transition : ne l\'enchante plus, remplace-la par du jaune.','Arme jaune (expéditions difficiles / transmutation).'],
  ['Casque','1 / 1','Heaume d\'Azphel','Unique',1540,5,'Ok','Au niveau du palier.',SRC_Y],
  ['Épaulettes','2 / 1','Spallières de campagne','Legend',1180,3,'Weak','Niveau d\'objet 1180 contre 1460 en médiane pour tes pièces d\'armure : c\'est ta pièce en retard.',SRC_Y],
  ['Torse','3 / 1','Cuirasse d\'expédition','Unique',1560,5,'Ok','Au niveau du palier.',SRC_Y],
  ['Jambières','4 / 1','Jambières d\'expédition','Unique',1500,4,'Enchant','Pièce à garder : monte-la à +5 (niveau sûr).',SRC_Y],
  ['Gants','5 / 1','Gantelets de campagne','Legend',1240,2,'Replace','Pièce bleue de transition : remplace-la par du jaune.',SRC_Y],
  ['Bottes','6 / 1','Solerets d\'expédition','Unique',1520,5,'Ok','Au niveau du palier.',SRC_Y],
  ['Cape','7 / 1','Cape du veilleur','Unique',1480,4,'Enchant','Pièce à garder : monte-la à +5.',SRC_Y],
  ['Collier','1 / 4','Collier d\'expédition','Unique',1450,3,'Enchant','Pièce à garder : monte-la à +5.',SRC_A],
  ['Boucle d\'oreille 1','2 / 4','Boucle du serment','Unique',1460,5,'Ok','Au niveau du palier.',SRC_A],
  ['Boucle d\'oreille 2','3 / 4','Boucle de campagne','Legend',1200,3,'Weak','Niveau d\'objet 1200 contre 1455 en médiane pour tes accessoires.',SRC_A],
  ['Anneau 1','4 / 4','Anneau d\'expédition','Unique',1470,4,'Enchant','Pièce à garder : monte-la à +5.',SRC_A],
  ['Anneau 2','5 / 4','Anneau d\'expédition','Unique',1470,5,'Ok','Au niveau du palier.',SRC_A],
  ['Bracelet 1','6 / 4','Bracelet d\'expédition','Unique',1440,4,'Enchant','Pièce à garder : monte-la à +5.',SRC_A],
  ['Bracelet 2','7 / 4','Bracelet de campagne','Legend',1190,3,'Replace','Pièce bleue de transition : remplace-la par du jaune.',SRC_A],
  ['Ceinture','8 / 4','Ceinture noble','Legend',1300,7,'Enchant','La ceinture noble ne se remplace pas : monte-la à +10 puis transmute-la vers le jaune.','Parchemins de ceinture noble : 2 par fort ou garnison de ta région.'],
  ['Rune 1','8 / 2','Rune du novice','Rare',1050,0,'Weak','Rune en retard sur ton palier.','Amulette et runes de meilleur grade (transmutation).'],
  ['Amulette','8 / 3',null,null,0,0,'Empty','Emplacement vide : c\'est le gain le plus rapide.','Amulette par transmutation.'],
];
const SEV = { Critique:[BAD,'✕'], Important:[WARN,'!'], Conseil:[INFO,'i'] };
// [sev, area, title, detail, action, screen, slot|stat|skill]
const FIND = {
  pve: [
    ['Critique','Équipement','Amulette manquante','Un emplacement vide coûte plus qu\'une pièce faible : c\'est le gain le plus rapide de ton palier.','Transmuter une amulette','gear',18],
    ['Important','Stats','Trop de Défense, pas assez de PV','Défense : 46 % de tes stats ajustables (repère 32 %). Survie : 17 % (repère 28 %).','Relancer les stats secondaires des accessoires vers les PV','stats',0],
    ['Important','Équipement','Épaulettes en retard (1180 contre 1460)','Ta pièce d\'armure la plus faible : la remplacer rapporte plus que d\'enchanter le reste.','Armures jaunes d\'expédition','gear',3],
    ['Important','Équipement','Gants de transition (bleu)','Pièce bleue sous le grade visé au palier « Passage au jaune ».','Ne plus enchanter, remplacer par du jaune','gear',6],
    ['Conseil','Compétences','Réduire « Frappe punitive » de 4 niveaux','Dégâts secondaires en JcE pour un Templier : les points rapportent plus en défensif.','Remettre ces 4 points dans « Bouclier divin »','skills',0],
    ['Conseil','Daevanion','Réorganiser Nezekan : +6 %','Deux cases en bout de chemin rapportent peu pour ta classe en JcE.','Retirer 2 cases, en prendre 5 vers les PV','daeva',null],
    ['Conseil','Enchantement','Monter l\'arme à +8','Pièce à garder au palier : l\'arme pèse le plus lourd.','Enchanter de +6 à +8','gear',0],
  ],
  pvp: [
    ['Critique','Stats','Pas assez de résistance JcJ','Les stats JcJ ne comptent qu\'en vue JcJ : tu en as 2 % (repère 14 %).','Pièces Abyssales avec stats JcJ','stats',7],
    ['Critique','Équipement','Amulette manquante','Un emplacement vide coûte plus qu\'une pièce faible.','Transmuter une amulette','gear',18],
    ['Important','Daevanion','Ouvrir 3 cases sur Azphel','Tes cristaux Azphel rapportent le plus sur le chemin du haut.','Prochaines cases : l.4 c.7, l.4 c.8, l.3 c.8','daeva',null],
    ['Important','Équipement','Main secondaire de transition','Bouclier bleu : remplace-le par un bouclier jaune Abyssal.','Boutique Abyssale','gear',1],
    ['Conseil','Compétences','Monter « Chaîne de jugement » à 10','Contrôle prioritaire en JcJ pour un Templier.','+3 points (réinitialisation gratuite)','skills',2],
    ['Conseil','Compétences','Mettre le stigma « Égide sacrée »','Réduction de dégâts reçus en burst JcJ.','Équiper à la place de « Lame vengeresse »','skills',null],
  ],
};
const SCORES = { pve:{overall:64,stats:71,gear:58,skills:66,daev:52}, pvp:{overall:57,stats:54,gear:61,skills:60,daev:48} };
const CATS = {
  pve:[['Attaque',22,22,'Dégâts infligés : ATQ, critique, précision.'],['Défense',46,32,'Réduction des dégâts reçus : DÉF, blocage, parade.'],['Survie',17,28,'Capacité à encaisser : PV, régénération, soins reçus.'],['Utilité',15,18,'Vitesse, résistances, PM.']],
  pvp:[['Attaque',22,20,'Dégâts infligés : ATQ, critique, précision.'],['Défense',46,30,'Réduction des dégâts reçus : DÉF, blocage, parade.'],['Survie',17,26,'Capacité à encaisser : PV, régénération, soins reçus.'],['Utilité',15,24,'Vitesse, résistances JcJ, PM.']],
};
// [nom, famille, total, part{pve,pvp}, repère{pve,pvp}, poids{pve,pvp}, desc, où, sources]
const STATS = [
  ['PV','Survie','18 450',[14,14],[22,20],[.9,.85],'Points de vie maximum.','Accessoires, cases Daevanion vertes, titres','Daevanion 38 %, Collier 22 %, Titres 15 %'],
  ['Défense','Défense','2 140',[31,31],[18,20],[.55,.6],'Réduit les dégâts physiques reçus.','Armures (stats secondaires), Daevanion','Daevanion 44 %, Torse 18 %, Casque 12 %'],
  ['Blocage','Défense','980',[9,9],[10,8],[.85,.7],'Chance de bloquer une attaque avec le bouclier.','Bouclier, Daevanion','Bouclier 51 %, Daevanion 30 %'],
  ['Parade','Défense','610',[6,6],[6,6],[.6,.6],'Chance de parer une attaque.','Arme, Daevanion','Arme 60 %, Daevanion 40 %'],
  ['ATQ','Attaque','612',[12,12],[12,10],[.5,.5],'Attaque physique de base.','Arme, anneaux, Daevanion','Arme 48 %, Anneaux 24 %'],
  ['Critique','Attaque','420',[6,6],[6,8],[.35,.45],'Chance de coup critique.','Boucles d\'oreille, Daevanion','Boucles 40 %, Daevanion 35 %'],
  ['Précision','Attaque','540',[4,4],[4,6],[.4,.5],'Réduit les chances d\'être esquivé.','Bracelets, Daevanion','Bracelets 55 %, Daevanion 30 %'],
  ['Résistance JcJ','Utilité','120',[2,2],[2,14],[.1,.95],'Réduit les dégâts reçus des joueurs (compte uniquement en JcJ).','Pièces Abyssales, Daevanion Azphel','Ceinture 100 %'],
];
// [type, nom, rôle, acheté, bonus, delta{pve,pvp}, prio{pve,pvp}, barre, pourquoi]
const SKILLS = [
  ['Active','Frappe punitive','Dégâts principaux',10,2,[-4,-2],[.45,.55],true,'Dégâts secondaires pour un tank en JcE : les points rapportent plus en défensif.'],
  ['Active','Bouclier divin','Défensif',6,1,[4,2],[.95,.85],true,'Réduction de dégâts majeure : indispensable en donjon.'],
  ['Active','Chaîne de jugement','Contrôle',7,0,[1,3],[.7,.95],true,'Attire et immobilise : prioritaire en JcJ.'],
  ['Active','Provocation','Aggro',8,1,[2,0],[.9,.3],true,'Maintient l\'aggro du groupe en JcE ; inutile en JcJ.'],
  ['Active','Châtiment','Burst',9,2,[0,0],[.6,.7],true,'Déjà au niveau visé.'],
  ['Active','Courroux céleste','Débuff',5,0,[-3,-1],[.35,.5],false,'Peu utile dans ta rotation actuelle (hors barre).'],
  ['Passive','Peau de pierre','Défensif',10,3,[0,0],[.85,.8],false,'Au maximum de base : seuls Daevanion et équipement la montent.'],
  ['Passive','Vigueur du gardien','Survie',4,0,[0,0],[.5,.5],false,'Passif à valider (non documenté par les guides).'],
];
const STIGMAS = [['Garder','Bouclier réflecteur','Défensif','8 → 10','6 fragments'],['Mettre','Égide sacrée','Défensif','3 → 8','14 fragments'],['Garder','Cri de défi','Aggro','10 → 10',''],['Retirer','Lame vengeresse','Dégâts','5 → 0','']];
const BOARDS = {
  pve:[['Nezekan','Commun',34,62,58,134,3],['Zikel','Commun',22,62,30,134,2],['Vaizel','Commun',4,62,4,134,1],['Ariel','Ariel',10,70,18,168,1]],
  pvp:[['Nezekan','Commun',34,62,58,134,3],['Zikel','Commun',22,62,30,134,2],['Vaizel','Commun',4,62,4,134,1],['Azphel','Azphel',6,90,10,232,1]],
};
const STEPS = [[4,8,'PV +100',true],[3,8,'passage',true],[3,9,'Bouclier divin +1',false],[2,9,'Blocage +30',false],[2,8,'PV +100',false]];
const REMOVED = [[9,3,'ATQ +3 · importance 0,30'],[10,4,'Critique +5 · importance 0,25']];
const PATH = ['8,6','8,5','9,5','9,4','9,3','10,4'];
export const HOW = 'Score global = moyenne pondérée des quatre notes. Stats : part de tes stats ajustables placée dans des stats utiles à ta classe. Équipement : pièces au niveau de ton palier. Compétences : niveaux visés atteints, pondérés par leur importance. Daevanion : part des plateaux utiles déjà ouverte. Les actions sont classées par gain attendu / ressources dépensées.';
export const SAVED = [{ name:'Lyra', sub:'Templier · Azphel (eu)', date:'22/10' },{ name:'Kaelis', sub:'Spiritualiste · Nezekan (eu)', date:'18/10' },{ name:'Morvenn', sub:'Gladiateur · Zikel (eu)', date:'02/10' }];
export const HITS = [{ name:'Lyra', cls:'Templier', server:'Azphel', lvl:45 },{ name:'Lyra', cls:'Rôdeur', server:'Triniel', lvl:38 },{ name:'Lyrael', cls:'Clerc', server:'Vaizel', lvl:45 }];

const sc = v => v >= 75 ? GOOD : v >= 50 ? WARN : BAD;
export const initial = () => ({ screen:'home', mode:'pve', slot:3, stat:0, skill:0, board:0, how:false, sp:3, dp:6 });
const SCREENS = [['home','Accueil','Accueil'],['stats','Stats','Stats'],['gear','Équipement','Équip.'],['skills','Compétences','Compét.'],['daeva','Daevanion','Daeva.'],['details','Détails','Détails']];

export function view(s, t, upd) {
  const m = s.mode, mi = m === 'pve' ? 0 : 1, S = SCORES[m];
  const go = {}; SCREENS.concat([['import']]).forEach(x => { go[x[0]] = () => upd({ screen:x[0] }); });
  const scoreOf = { home:S.overall, stats:S.stats, gear:S.gear, skills:S.skills, daeva:S.daev, details:null };
  const nav = SCREENS.map(x => { const on = x[0] === s.screen, v = scoreOf[x[0]];
    return { key:x[0], label:x[1], short:x[2], score:v ?? '···', scoreColor:v == null ? t.navMute : sc(v), bg:on ? t.navBg : 'transparent', fg:on ? t.navFg : t.navMute, border:on ? t.navBorder : 'transparent', glow:on && t.glow ? `0 0 14px ${t.accent}59` : 'none', onClick:go[x[0]] }; });
  const tiles = SLOTS.map((r, k) => {
    const g = GRADES[r[3]] || ['—','#6A6A72'], st = STATUS[r[6]], on = s.slot === k && s.screen === 'gear', gc = r[2] ? g[1] : '#6A6A72';
    return { name:r[0], area:r[1], item:r[2] || 'Emplacement vide', gradeName:r[2] ? g[0] : '—', gradeColor:gc, power:r[4] || '—', enchTxt:r[2] ? '+' + r[5] : '', enchFull:r[2] ? '+' + r[5] + ' / 15' : '—',
      statusLabel:st[0], statusColor:st[1], code:st[2], advice:r[7], source:r[8], ok:r[6] === 'Ok',
      bg:on ? t.tileSel : t.tile, border:on ? (t.glow ? gc : t.borderSel) : (t.glow ? gc + '55' : t.border),
      glow:t.glow ? (on ? `0 0 0 1px ${gc}, 0 0 22px ${gc}66` : `0 0 12px ${gc}1f`) : 'none',
      onClick:() => upd({ slot:k, screen:'gear' }) };
  });
  const finding = (x, k) => ({ key:k, sev:x[0], sevColor:SEV[x[0]][0], icon:SEV[x[0]][1], area:x[1], title:x[2], detail:x[3], action:x[4],
    onClick:() => upd(x[5] === 'gear' ? { screen:'gear', slot:x[6] } : x[5] === 'stats' ? { screen:'stats', stat:x[6] } : x[5] === 'skills' && x[6] != null ? { screen:'skills', skill:x[6] } : { screen:x[5] }) });
  const all = FIND[m].map(finding);
  const groups = [...new Set(all.map(f => f.area))].map(a => ({ label:a, count:all.filter(f => f.area === a).length, items:all.filter(f => f.area === a) }));
  const seg = on => ({ bg:on ? t.segBg : 'transparent', fg:on ? t.segFg : t.segMute, glow:on && t.glow ? `0 0 14px ${t.accent}8c` : 'none' });
  const pv = seg(m === 'pve'), pp = seg(m === 'pvp');
  const parts = [['Stats',S.stats,'stats'],['Équipement',S.gear,'gear'],['Compétences',S.skills,'skills'],['Daevanion',S.daev,'daeva']].map(p => ({ label:p[0], v:p[1], pct:p[1] + '%', color:sc(p[1]), onClick:go[p[2]] }));
  // Stats
  const cats = CATS[m].map(c => { const gap = c[1] - c[2], col = Math.abs(gap) <= 4 ? GOOD : gap > 0 ? WARN : BAD;
    return { label:c[0], share:c[1] + ' %', target:c[2] + ' %', pct:Math.min(c[1] * 1.6, 100) + '%', targetPos:Math.min(c[2] * 1.6, 100) + '%', color:col, desc:c[3], gapTxt:Math.abs(gap) <= 4 ? 'équilibré' : gap > 0 ? `+${gap} pts de trop` : `${gap} pts manquants` }; });
  const stats = STATS.map((r, k) => { const sh = r[3][mi], tg = r[4][mi], w = r[5][mi], on = s.stat === k;
    const col = tg - sh > 2 ? BAD : sh - tg > 8 ? WARN : t.text;
    const verdict = tg - sh > 2 ? `Il te manque environ ${tg - sh} points de part : à augmenter.` : sh - tg > 8 ? `Tu y mets ${sh - tg} points de part de trop : à réduire au profit des stats manquantes.` : 'Proche du repère de ta classe.';
    return { name:r[0], cat:r[1], total:r[2], share:sh + ' %', target:tg + ' %', weight:w.toFixed(2), weightPct:w * 100 + '%', shareColor:col, desc:r[6], where:r[7], sources:r[8], verdict, verdictColor:col === t.text ? GOOD : col,
      bg:on ? t.rowSel : 'transparent', border:on ? t.borderSel : 'transparent', onClick:() => upd({ stat:k }) }; });
  // Compétences
  const skills = SKILLS.map((r, k) => { const d = r[5][mi], on = s.skill === k;
    return { kind:r[0] === 'Active' && r[7] ? 'Active ✓' : r[0], name:r[1], role:r[2], base:r[3], bonus:'+' + r[4], total:r[3] + r[4],
      delta:d === 0 ? '=' : d > 0 ? `+${d} → ${r[3] + d}` : `${d} → ${r[3] + d}`, deltaColor:d === 0 ? t.navMute : d > 0 ? GOOD : BAD,
      deltaBg:d === 0 ? 'transparent' : (d > 0 ? GOOD : BAD) + '26', prio:r[6][mi].toFixed(2), prioPct:r[6][mi] * 100 + '%', why:r[8], target:r[3] + d,
      bg:on ? t.rowSel : 'transparent', border:on ? t.borderSel : 'transparent', onClick:() => upd({ skill:k }) }; });
  const stigmas = STIGMAS.map(x => ({ label:x[0], color:x[0] === 'Retirer' ? BAD : x[0] === 'Mettre' ? INFO : GOOD, name:x[1], role:x[2], lvl:x[3], cost:x[4] }));
  // Daevanion
  const bl = BOARDS[m], bi = Math.min(s.board, bl.length - 1), B = bl[bi];
  const boards = bl.map((b, k) => ({ name:b[0], sub:`${b[2]}/${b[3]}`, bg:k === bi ? t.rowSel : 'transparent', fg:k === bi ? t.navFg : t.navMute, border:k === bi ? t.borderSel : t.border, onClick:() => upd({ board:k }) }));
  const pools = [['Commun',92,s.dp,134],[m === 'pve' ? 'Ariel' : 'Azphel',m === 'pve' ? 18 : 10,0,m === 'pve' ? 168 : 232]].map(p => ({ name:p[0], spent:p[1], unspent:p[2], fill:p[3] }));
  const cells = [];
  for (let r = 0; r < 15; r++) for (let c = 0; c < 15; c++) {
    const dist = Math.abs(r - 7) + Math.abs(c - 7), exists = dist <= 7 && (r * 7 + c * 3 + bi) % 11 !== 0;
    const h = (r * 13 + c * 7 + bi * 3) % 10, grade = h < 5 ? 'Common' : h < 8 ? 'Rare' : h < 9 ? 'Legend' : 'Unique';
    const start = r === 7 && c === 7, open = start || dist <= B[6] || (bi === 0 && PATH.includes(r + ',' + c));
    const step = bi === 0 ? STEPS.findIndex(x => x[0] === r && x[1] === c) : -1, rem = bi === 0 && REMOVED.some(x => x[0] === r && x[1] === c);
    const col = start ? '#FFFFFF' : GRADES[grade][1];
    cells.push(!exists ? { bg:'transparent', border:'transparent', op:0, rad:'3px', label:'', lc:'transparent', glow:'none' } : {
      bg:rem ? t.cellOff : open ? col : t.cellOff, border:rem ? BAD : step >= 0 ? t.accent : open ? col : col + '55', op:open || step >= 0 || rem ? 1 : .55,
      rad:grade === 'Legend' ? '50%' : '3px', label:rem ? '✕' : step >= 0 ? String(step + 1) : '', lc:rem ? BAD : step >= 0 ? t.accent : 'transparent',
      glow:t.glow && (open || step >= 0) ? `0 0 8px ${step >= 0 ? t.accent : col}66` : 'none', bw:rem || step >= 0 ? '2px' : '1px' });
  }
  const steps = bi === 0 ? STEPS.map((x, k) => ({ n:k + 1, txt:`${B[0]} l.${x[0] + 1} c.${x[1] + 1} — ${x[2]}`, tag:x[3] ? 'faisable maintenant' : '', color:x[2] === 'passage' ? t.navMute : x[3] ? GOOD : t.text })) : [];
  const removed = bi === 0 ? REMOVED.map(x => ({ txt:`${B[0]} l.${x[0] + 1} c.${x[1] + 1} — ${x[2]}` })) : [];
  const titles = { home:'Accueil', stats:'Stats', gear:'Équipement', skills:'Compétences', daeva:'Daevanion', details:'Détails' };
  const todo = tiles.filter(x => !x.ok).length;
  return {
    isImport:s.screen === 'import', isApp:s.screen !== 'import', isHome:s.screen === 'home', isGear:s.screen === 'gear', isDoll:s.screen === 'home' || s.screen === 'gear',
    isStats:s.screen === 'stats', isSkills:s.screen === 'skills', isDaeva:s.screen === 'daeva', isDetails:s.screen === 'details', hasRight:s.screen !== 'details',
    title:titles[s.screen] || '', go, nav, tiles, sel:tiles[s.slot],
    pve:() => upd({ mode:'pve' }), pvp:() => upd({ mode:'pvp' }), pveBg:pv.bg, pveFg:pv.fg, pveGlow:pv.glow, pvpBg:pp.bg, pvpFg:pp.fg, pvpGlow:pp.glow,
    modeLabel:m === 'pve' ? 'vue JcE' : 'vue JcJ', modeShort:m === 'pve' ? 'JcE' : 'JcJ',
    sc:{ overall:S.overall, overallColor:sc(S.overall), parts }, actions:all.slice(0, 3), all, allCount:all.length, groups,
    how:s.how, howIcon:s.how ? '−' : '+', toggleHow:() => upd({ how:!s.how }), howText:HOW,
    cats, stats, statSel:stats[s.stat], efficiency:m === 'pve' ? '72 %' : '58 %',
    skills, skillSel:skills[s.skill], stigmas, sp:s.sp, dp:s.dp, spent:38, budget:38 + s.sp,
    spMinus:() => upd({ sp:Math.max(0, s.sp - 1) }), spPlus:() => upd({ sp:s.sp + 1 }),
    dpMinus:() => upd({ dp:Math.max(0, s.dp - 1) }), dpPlus:() => upd({ dp:s.dp + 1 }),
    pools, boards, board:{ name:B[0], pool:B[1], open:B[2], total:B[3], spent:B[4], pts:B[5] }, cells, steps, removed, hasPlan:bi === 0, noPlan:bi !== 0,
    statusTodo:todo + ' pièces à traiter', saved:SAVED, hits:HITS,
  };
}

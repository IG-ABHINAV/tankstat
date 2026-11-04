// The colour steps Tankstat's look is made of, copied once from Radix Colors (its "sand" gray, "indigo" accent, the status hues and
// the chart series), so the look stays the same without Radix. Plain constants without MUI: eagerly loaded code and the Vite config
// (the colour mode script) use them too.

export type Scheme = 'light' | 'dark'

/** A hue's steps: 3 and a3 tint a surface, 4/a4 its hover, 8 a border, 9 is the solid colour, 10 its hover, 11 readable text, 12 high contrast. */
export interface HueSteps {
  3: string
  a3: string
  a4: string
  a5: string
  a7: string
  8: string
  9: string
  10: string
  11: string
  12: string
}

/** The gray (Radix "sand"), steps 1..12 and their alpha versions a1..a12. */
export const SAND: Record<Scheme, readonly string[]> = {
  dark: ['#111110', '#191918', '#222221', '#2a2a28', '#31312e', '#3b3a37', '#494844', '#62605b', '#6f6d66', '#7c7b74', '#b5b3ad', '#eeeeec'],
  light: ['#fdfdfc', '#f9f9f8', '#f1f0ef', '#e9e8e6', '#e2e1de', '#dad9d6', '#cfceca', '#bcbbb5', '#8d8d86', '#82827c', '#63635e', '#21201c'],
}
export const SAND_A: Record<Scheme, readonly string[]> = {
  dark: ['#00000000', '#f4f4f309', '#f6f6f513', '#fefef31b', '#fbfbeb23', '#fffaed2d', '#fffbed3c', '#fff9eb57', '#fffae965', '#fffdee73', '#fffcf4b0', '#fffffded'],
  light: ['#55550003', '#25250007', '#20100010', '#1f150019', '#1f180021', '#19130029', '#19140035', '#1915014a', '#0f0f0079', '#0c0c0083', '#080800a1', '#060500e3'],
}

/** Step n (1-based) of a 12-step scale. */
export const step = (scale: readonly string[], n: number) => scale[n - 1]

export const HUES: Record<'indigo' | 'red' | 'amber' | 'green' | 'blue', Record<Scheme, HueSteps>> = {
  indigo: {
    dark: { 3: '#182449', a3: '#2f62ff3c', a4: '#3566ff57', a5: '#4171fd6b', a7: '#5a7fff90', 8: '#435db1', 9: '#3e63dd', 10: '#5472e4', 11: '#9eb1ff', 12: '#d6e1ff' },
    light: { 3: '#edf2fe', a3: '#0047f112', a4: '#0044ff1e', a5: '#0044ff2d', a7: '#0037ed54', 8: '#8da4ef', 9: '#3e63dd', 10: '#3358d4', 11: '#3a5bc7', 12: '#1f2d5c' },
  },
  red: {
    dark: { 3: '#3b1219', a3: '#ff173f2d', a4: '#fe0a3b44', a5: '#ff204756', a7: '#ff536184', 8: '#b54548', 9: '#e5484d', 10: '#ec5d5e', 11: '#ff9592', 12: '#ffd1d9' },
    light: { 3: '#feebec', a3: '#f3000d14', a4: '#ff000824', a5: '#ff000632', a7: '#df000356', 8: '#eb8e90', 9: '#e5484d', 10: '#dc3e42', 11: '#ce2c31', 12: '#641723' },
  },
  amber: {
    dark: { 3: '#302008', a3: '#fa820022', a4: '#fc820032', a5: '#fd8b0041', a7: '#ffab2567', 8: '#8f6424', 9: '#ffc53d', 10: '#ffd60a', 11: '#ffca16', 12: '#ffe7b3' },
    light: { 3: '#fff7c2', a3: '#ffde003d', a4: '#ffd40063', a5: '#f8cf0088', a7: '#dc9b009d', 8: '#e2a336', 9: '#ffc53d', 10: '#ffba18', 11: '#ab6400', 12: '#4f3422' },
  },
  green: {
    dark: { 3: '#132d21', a3: '#22ff991e', a4: '#11ff992d', a5: '#2bffa23c', a7: '#50fdac5e', 8: '#2f7c57', 9: '#30a46c', 10: '#33b074', 11: '#3dd68c', 12: '#b1f1cb' },
    light: { 3: '#e6f6eb', a3: '#00a43319', a4: '#00a83829', a5: '#019c393b', a7: '#00914071', 8: '#5bb98b', 9: '#30a46c', 10: '#2b9a66', 11: '#218358', 12: '#193b2d' },
  },
  blue: {
    dark: { 3: '#0d2847', a3: '#0077ff3a', a4: '#0075ff57', a5: '#0081fd6b', a7: '#2a91fe98', 8: '#2870bd', 9: '#0090ff', 10: '#3b9eff', 11: '#70b8ff', 12: '#c2e6ff' },
    light: { 3: '#e6f4fe', a3: '#008ff519', a4: '#009eff2a', a5: '#0093ff3d', a7: '#0083eb71', 8: '#5eb1ef', 9: '#0090ff', 10: '#0588f0', 11: '#0d74ce', 12: '#113264' },
  },
}

/**
 * The solid fills under white text (#235): Radix's step 9 is no contrast step, and with white on it only indigo passes WCAG's 4.5:1 (red
 * 3.91, green 3.16, blue 3.26), while step 10, its hover, is lighter than 9 in the dark scheme (indigo 4.28). So these are step 9 darkened
 * in sRGB until white reaches 4.5:1 with a margin (indigo already does), and a darker hover; step 9 is the same in both schemes, so one
 * pair serves both. Amber carries dark text and passes on its own steps (`theme/palette.ts`).
 */
export const SOLID: Record<'indigo' | 'red' | 'green' | 'blue', { rest: string; hover: string }> = {
  indigo: { rest: '#3e63dd', hover: '#3859c7' }, // 5.21 and 6.14 with white
  red: { rest: '#ca3f44', hover: '#b73a3e' }, // 4.88 and 5.69
  green: { rest: '#258054', hover: '#22734c' }, // 4.89 and 5.79
  blue: { rest: '#0072c9', hover: '#0066b5' }, // 4.94 and 5.89
}

/** Chart series: step 9 of indigo, cyan, amber, grass, tomato, violet, teal and pink (the same in both schemes). */
export const SERIES_COLORS = ['#3e63dd', '#00a2c7', '#ffc53d', '#46a758', '#e54d2e', '#6e56cf', '#12a594', '#d6409f'] as const

/**
 * The gradients behind a vehicle without a picture: dark step-8 pairs in both schemes, since white text sits on them (under a scrim)
 * whatever the scheme.
 */
export const PLACEHOLDER_PAIRS = [
  ['#435db1', '#11809c'],
  ['#207e73', '#3e7949'],
  ['#6958ad', '#a84885'],
  ['#a35829', '#ac4d39'],
  ['#2870bd', '#197cae'],
  ['#92549c', '#b0436e'],
] as const

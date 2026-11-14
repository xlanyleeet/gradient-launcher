<template>
	<div ref="container" class="codemirror-editor-root size-full overflow-hidden" />
</template>

<script setup lang="ts">
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands'
import { markdown } from '@codemirror/lang-markdown'
import { Compartment, EditorState } from '@codemirror/state'
import {
	EditorView,
	highlightActiveLine,
	highlightActiveLineGutter,
	keymap,
	lineNumbers,
} from '@codemirror/view'
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'

const props = defineProps<{
	modelValue: string
	readonly?: boolean
	language?: string
}>()

const emit = defineEmits<{
	'update:modelValue': [val: string]
	save: []
	toggleFind: []
	openReplace: []
}>()

const container = ref<HTMLElement | null>(null)
let view: EditorView | null = null

const readOnlyCompartment = new Compartment()
const languageCompartment = new Compartment()

let currentMatches: Array<{ from: number; to: number }> = []
let activeMatchIndex = -1

const darkTheme = EditorView.theme(
	{
		'&': {
			height: '100%',
			color: 'var(--color-text-primary, #e2e8f0)',
			backgroundColor: 'var(--color-bg-raised, #18181b)',
			fontSize: '0.875rem',
			fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
		},
		'.cm-content': {
			caretColor: '#c77dff',
			padding: '12px 0',
		},
		'.cm-cursor, .cm-dropCursor': {
			borderLeftColor: '#c77dff',
			borderLeftWidth: '2px',
		},
		'&.cm-focused .cm-selectionBackground, ::selection, .cm-selectionBackground': {
			backgroundColor: 'rgba(157, 78, 221, 0.35) !important',
		},
		'.cm-gutters': {
			backgroundColor: 'var(--color-bg-raised, #18181b)',
			color: 'var(--color-text-secondary, #71717a)',
			borderRight: '1px solid var(--color-divider, rgba(255, 255, 255, 0.08))',
			paddingRight: '6px',
		},
		'.cm-activeLine': {
			backgroundColor: 'rgba(255, 255, 255, 0.03)',
		},
		'.cm-activeLineGutter': {
			backgroundColor: 'rgba(255, 255, 255, 0.06)',
			color: 'var(--color-text-contrast, #ffffff)',
		},
		'.cm-scroller': {
			overflow: 'auto',
			fontFamily: 'inherit',
		},
	},
	{ dark: true },
)

function getLanguageExtension(lang?: string) {
	if (lang === 'markdown' || lang === 'md') return markdown()
	return []
}

onMounted(() => {
	if (!container.value) return

	const startState = EditorState.create({
		doc: props.modelValue,
		extensions: [
			lineNumbers(),
			highlightActiveLineGutter(),
			highlightActiveLine(),
			history(),
			darkTheme,
			readOnlyCompartment.of(EditorState.readOnly.of(props.readonly ?? false)),
			languageCompartment.of(getLanguageExtension(props.language)),
			keymap.of([
				{
					key: 'Mod-s',
					run: () => {
						emit('save')
						return true
					},
				},
				{
					key: 'Mod-f',
					run: () => {
						emit('toggleFind')
						return true
					},
				},
				{
					key: 'Mod-h',
					run: () => {
						emit('openReplace')
						return true
					},
				},
				indentWithTab,
				...defaultKeymap,
				...historyKeymap,
			]),
			EditorView.updateListener.of((update) => {
				if (update.docChanged) {
					emit('update:modelValue', update.state.doc.toString())
				}
			}),
		],
	})

	view = new EditorView({
		state: startState,
		parent: container.value,
	})
})

watch(
	() => props.modelValue,
	(newValue) => {
		if (view && view.state.doc.toString() !== newValue) {
			view.dispatch({
				changes: { from: 0, to: view.state.doc.length, insert: newValue },
			})
		}
	},
)

watch(
	() => props.readonly,
	(readOnly) => {
		if (view) {
			view.dispatch({
				effects: readOnlyCompartment.reconfigure(EditorState.readOnly.of(readOnly ?? false)),
			})
		}
	},
)

watch(
	() => props.language,
	(lang) => {
		if (view) {
			view.dispatch({
				effects: languageCompartment.reconfigure(getLanguageExtension(lang)),
			})
		}
	},
)

function focus() {
	view?.focus()
}

let lastFindQuery = ''

function find(query: string, { wrap = true, caseSensitive = false } = {}) {
	lastFindQuery = query
	if (!view || !query) {
		currentMatches = []
		activeMatchIndex = -1
		return
	}

	const doc = view.state.doc.toString()
	const regex = new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), caseSensitive ? 'g' : 'gi')
	currentMatches = []
	let match: RegExpExecArray | null

	while ((match = regex.exec(doc)) !== null) {
		currentMatches.push({ from: match.index, to: match.index + match[0].length })
	}

	if (currentMatches.length > 0) {
		activeMatchIndex = 0
		selectMatch(activeMatchIndex)
	} else {
		activeMatchIndex = -1
	}
}

function selectMatch(index: number) {
	if (!view || index < 0 || index >= currentMatches.length) return
	const m = currentMatches[index]
	view.dispatch({
		selection: { anchor: m.from, head: m.to },
		scrollIntoView: true,
	})
}

function findNext() {
	if (currentMatches.length === 0) return
	activeMatchIndex = (activeMatchIndex + 1) % currentMatches.length
	selectMatch(activeMatchIndex)
}

function findPrevious() {
	if (currentMatches.length === 0) return
	activeMatchIndex = (activeMatchIndex - 1 + currentMatches.length) % currentMatches.length
	selectMatch(activeMatchIndex)
}

function replace(replacement: string) {
	if (!view || activeMatchIndex < 0 || activeMatchIndex >= currentMatches.length) return
	const m = currentMatches[activeMatchIndex]
	view.dispatch({
		changes: { from: m.from, to: m.to, insert: replacement },
	})
}

function replaceAll(arg1: string, arg2?: string) {
	let query = lastFindQuery
	let replacement = arg1
	if (arg2 !== undefined) {
		query = arg1
		replacement = arg2
	}
	if (!view || !query) return
	const doc = view.state.doc.toString()
	const escaped = query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
	const newDoc = doc.replace(new RegExp(escaped, 'gi'), replacement)
	view.dispatch({
		changes: { from: 0, to: view.state.doc.length, insert: newDoc },
	})
}

onBeforeUnmount(() => {
	view?.destroy()
	view = null
})

defineExpose({
	focus,
	find,
	findNext,
	findPrevious,
	replace,
	replaceAll,
})
</script>

<style scoped>
.codemirror-editor-root :deep(.cm-editor) {
	height: 100%;
}
</style>

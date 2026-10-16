<template>
	<NewModal ref="modal" :header="formatMessage(messages.header)" max-width="544px" no-padding>
		<div class="grid grid-cols-[1fr_auto] gap-2.5 h-[154px] px-7 pt-4 pb-1 pr-9">
			<div class="flex flex-col gap-2.5 items-start justify-center h-min mt-5">
				<div class="font-semibold text-xl text-contrast">
					{{ formatMessage(messages.descriptionHeader) }}
				</div>
				<div class="text-secondary leading-6">
					{{ formatMessage(messages.description) }}
				</div>
			</div>
			<div class="relative h-full w-[96px] overflow-hidden mx-3">
				<div class="absolute top-0 left-0 z-0 w-full flex grow-0 flex-col items-end p-0">
					<img :src="steveImage" alt="" class="self-stretch" />
				</div>
				<div
					class="absolute left-0 bottom-0 z-10 order-1 h-6 w-[120px] shrink-0 grow-0 bg-[linear-gradient(180deg,rgba(39,41,46,0)_0%,#27292E_80%,#27292E_100%)]"
				></div>
			</div>
		</div>

		<div class="flex flex-col gap-6 px-6 pb-6">
			<div class="grid grid-cols-2 gap-2">
				<ButtonLink href="https://support.modrinth.com" @click="modal?.hide()">
					<MessagesSquareIcon />
					{{ formatMessage(messages.getSupport) }}
				</ButtonLink>
				<Button type="colored" color="brand" :disabled="loadingSignIn" @click="signIn">
					<SpinnerIcon v-if="loadingSignIn" class="animate-spin" />
					<svg
						v-else
						width="20"
						height="20"
						viewBox="0 0 20 20"
						fill="none"
						xmlns="http://www.w3.org/2000/svg"
					>
						<rect width="9.25" height="9.25" fill="black" fill-opacity="0.9" />
						<rect x="10.75" width="9.25" height="9.25" fill="black" fill-opacity="0.9" />
						<rect y="10.75" width="9.25" height="9.25" fill="black" fill-opacity="0.9" />
						<rect x="10.75" y="10.75" width="9.25" height="9.25" fill="black" fill-opacity="0.9" />
					</svg>
					{{ formatMessage(messages.signIn) }}
				</Button>
			</div>
			<div class="flex items-center gap-2">
				<div class="h-[1px] bg-surface-5 flex-1"></div>
				<span class="text-xs text-secondary font-medium uppercase tracking-wider">або грати офлайн (піратка)</span>
				<div class="h-[1px] bg-surface-5 flex-1"></div>
			</div>
			<div class="flex gap-2">
				<Input
					v-model="offlineUsername"
					placeholder="Нікнейм гравця"
					wrapper-class="flex-1"
					@keydown.enter="playOffline"
				/>
				<Button
					type="colored"
					color="brand"
					:disabled="!offlineUsername.trim() || loadingSignIn"
					@click="playOffline"
				>
					Грати офлайн
				</Button>
			</div>
		</div>
	</NewModal>
</template>

<script setup lang="ts">
import { MessagesSquareIcon, SpinnerIcon } from '@modrinth/assets'
import { Button, ButtonLink, defineMessages, Input, NewModal, useVIntl } from '@modrinth/ui'
import { ref } from 'vue'

import steveImage from '@/assets/steve-look-up-left.webp'
import { handleSevereError } from '@/composables/use-error.js'
import { injectMinecraftAccounts } from '@/features/minecraft-accounts/context'
import { trackEvent } from '@/helpers/analytics'
import { login as loginFlow, login_offline, set_default_user } from '@/helpers/auth.js'

const { formatMessage } = useVIntl()
const minecraftAccounts = injectMinecraftAccounts()

const messages = defineMessages({
	header: {
		id: 'minecraft-required.header',
		defaultMessage: 'Minecraft required',
	},
	descriptionHeader: {
		id: 'minecraft-required.description-header',
		defaultMessage: 'Sign in to Minecraft or Play Offline',
	},
	description: {
		id: 'minecraft-required.description',
		defaultMessage:
			'Sign in with your Microsoft account, or type your desired offline nickname to start playing immediately in cracked/offline mode.',
	},
	getSupport: {
		id: 'minecraft-required.get-support',
		defaultMessage: 'Get support',
	},
	signIn: {
		id: 'minecraft-required.sign-in',
		defaultMessage: 'Sign in to Microsoft',
	},
})

const modal = ref<InstanceType<typeof NewModal>>()
const loadingSignIn = ref(false)
const offlineUsername = ref('Player')

function show() {
	modal.value?.show()
}

async function signIn() {
	loadingSignIn.value = true

	try {
		const loggedIn = await loginFlow()
		if (!loggedIn) return

		await set_default_user(loggedIn.profile.id)
		await minecraftAccounts.value?.refreshValues()
		await trackEvent('AccountLogIn', { source: 'MinecraftRequiredModal' })
		modal.value?.hide()
	} catch (error) {
		handleSevereError(error)
	} finally {
		loadingSignIn.value = false
	}
}

async function playOffline() {
	if (!offlineUsername.value.trim()) return
	loadingSignIn.value = true

	try {
		const cred = await login_offline(offlineUsername.value.trim())
		if (!cred) return

		await set_default_user(cred.profile.id)
		await minecraftAccounts.value?.refreshValues()
		modal.value?.hide()
	} catch (error) {
		handleSevereError(error)
	} finally {
		loadingSignIn.value = false
	}
}

defineExpose({
	show,
})
</script>

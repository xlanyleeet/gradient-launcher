<script setup lang="ts">
import {
	CheckIcon,
	LogInIcon,
	PlusIcon,
	RadioButtonCheckedIcon,
	RadioButtonIcon,
	SpinnerIcon,
	TrashIcon,
	UserIcon,
	XIcon,
} from '@modrinth/assets'
import {
	Avatar,
	Button,
	IconButton,
	injectNotificationManager,
	Input,
	NewModal,
} from '@modrinth/ui'
import { computed, ref } from 'vue'

import {
	get_default_user,
	login as login_flow,
	login_offline,
	remove_user,
	set_default_user,
	users,
} from '@/helpers/auth'
import { getPlayerHeadUrl } from '@/helpers/rendering/player-head'
import type { Skin } from '@/helpers/skins'
import { get_available_skins } from '@/helpers/skins'

type MinecraftCredential = {
	profile: {
		id: string
		name: string
	}
	access_token?: string
	refresh_token?: string
}

const emit = defineEmits<{
	accountChanged: [account: MinecraftCredential | undefined]
}>()

const modal = ref<InstanceType<typeof NewModal> | null>(null)
const { addNotification, handleError } = injectNotificationManager()

const accounts = ref<MinecraftCredential[]>([])
const defaultUser = ref<string | undefined>()
const loginDisabled = ref(false)
const offlineUsername = ref('')
const showOfflineInput = ref(false)
const equippedSkin = ref<Skin | null>(null)
const equippedHeadUrl = ref<string>()
let headRequest = 0

const selectedAccount = computed(() =>
	accounts.value.find((acc) => acc.profile.id === defaultUser.value),
)

async function updateHeadUrl(skin: Skin | null) {
	const request = ++headRequest
	if (equippedHeadUrl.value) URL.revokeObjectURL(equippedHeadUrl.value)
	equippedHeadUrl.value = undefined
	if (!skin) return
	const url = await getPlayerHeadUrl(skin)
	if (request !== headRequest) URL.revokeObjectURL(url)
	else equippedHeadUrl.value = url
}

async function refreshValues() {
	defaultUser.value = await get_default_user().catch(handleError)
	const userList = await users().catch(handleError)
	accounts.value = Array.isArray(userList) ? [...userList] : []
	accounts.value.sort((a, b) => (a.profile?.name ?? '').localeCompare(b.profile?.name ?? ''))

	try {
		const skins = await get_available_skins()
		equippedSkin.value = skins.find((skin) => skin.is_equipped) ?? null
		await updateHeadUrl(equippedSkin.value)
	} catch {
		equippedSkin.value = null
		void updateHeadUrl(null)
	}

	emit('accountChanged', selectedAccount.value)
}

function getAccountAvatarUrl(account: MinecraftCredential): string {
	if (equippedSkin.value?.texture_key && selectedAccount.value?.profile?.id === account.profile.id) {
		return equippedHeadUrl.value ?? `https://mc-heads.net/avatar/${equippedSkin.value.texture_key}/64`
	}
	if (account.access_token === '0') {
		return `https://skin.ely.by/usernames/${encodeURIComponent(account.profile.name)}`
	}
	if (account.profile?.id) {
		return `https://mc-heads.net/avatar/${account.profile.id}/64`
	}
	return 'https://launcher-files.modrinth.com/assets/steve_head.png'
}

async function selectAccount(account: MinecraftCredential) {
	try {
		await set_default_user(account.profile.id)
		defaultUser.value = account.profile.id
		addNotification({
			title: 'Акаунт активовано',
			text: `Вибрано гравця ${account.profile.name}`,
			type: 'success',
		})
		await refreshValues()
	} catch (error) {
		handleError(error)
	}
}

async function removeAccount(id: string) {
	try {
		await remove_user(id)
		addNotification({
			title: 'Акаунт видалено',
			text: 'Акаунт було успішно прибрано зі списку.',
			type: 'info',
		})
		await refreshValues()
	} catch (error) {
		handleError(error)
	}
}

async function loginMicrosoft() {
	loginDisabled.value = true
	try {
		await login_flow()
		addNotification({
			title: 'Успішний вхід',
			text: 'Microsoft акаунт підключено!',
			type: 'success',
		})
		await refreshValues()
	} catch (error) {
		handleError(error)
	} finally {
		loginDisabled.value = false
	}
}

async function addOffline(name: string) {
	const trimmed = name.trim()
	if (!trimmed) return
	loginDisabled.value = true
	try {
		await login_offline(trimmed)
		offlineUsername.value = ''
		showOfflineInput.value = false
		addNotification({
			title: 'Офлайн акаунт додано',
			text: `Ласкаво просимо, ${trimmed}!`,
			type: 'success',
		})
		await refreshValues()
	} catch (error) {
		handleError(error)
	} finally {
		loginDisabled.value = false
	}
}

function show() {
	showOfflineInput.value = false
	offlineUsername.value = ''
	modal.value?.show()
	void refreshValues()
}

function hide() {
	modal.value?.hide()
}

function setLoginDisabled(disabled: boolean) {
	loginDisabled.value = disabled
}

async function setEquippedSkin(skin: Skin) {
	equippedSkin.value = skin
	try {
		await updateHeadUrl(skin)
	} catch (error) {
		handleError(error)
	}
}

defineExpose({
	show,
	hide,
	refreshValues,
	selectedAccount,
	accounts,
	loginDisabled,
	setLoginDisabled,
	setEquippedSkin,
	login: loginMicrosoft,
})
</script>

<template>
	<NewModal
		ref="modal"
		header="Акаунти Minecraft"
		max-width="540px"
		width="540px"
	>
		<div class="flex flex-col gap-4 py-1">
			<!-- Current Active Account Banner -->
			<div
				v-if="selectedAccount"
				class="flex items-center gap-3 p-3 rounded-2xl bg-surface-3 border border-solid border-surface-5"
			>
				<Avatar :src="getAccountAvatarUrl(selectedAccount)" size="44px" class="rounded-xl shadow-sm" />
				<div class="flex flex-col min-w-0 flex-1">
					<div class="flex items-center gap-2">
						<span class="text-base font-bold text-contrast truncate">{{ selectedAccount.profile.name }}</span>
						<span
							v-if="selectedAccount.access_token === '0'"
							class="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-purple/20 text-purple border border-purple/30"
						>
							Піратка / Офлайн
						</span>
						<span
							v-else
							class="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-brand/20 text-brand border border-brand/30"
						>
							Офіційний Microsoft
						</span>
					</div>
					<span class="text-xs text-secondary mt-0.5 flex items-center gap-1">
						<CheckIcon class="size-3.5 text-brand" />
						Активний для запуску гри
					</span>
				</div>
			</div>

			<!-- Accounts List -->
			<div v-if="accounts.length > 0" class="flex flex-col gap-2">
				<span class="text-xs font-semibold text-secondary uppercase tracking-wider px-1">
					Збережені акаунти ({{ accounts.length }})
				</span>
				<div class="flex flex-col gap-1.5 max-h-[220px] overflow-y-auto pr-1">
					<div
						v-for="acc in accounts"
						:key="acc.profile.id"
						class="flex items-center justify-between p-2.5 rounded-xl border border-solid transition-all cursor-pointer"
						:class="
							selectedAccount?.profile.id === acc.profile.id
								? 'bg-surface-4 border-brand/40 shadow-sm'
								: 'bg-button-bg border-surface-5 hover:bg-surface-3'
						"
						@click="selectAccount(acc)"
					>
						<div class="flex items-center gap-2.5 min-w-0 flex-1">
							<RadioButtonCheckedIcon
								v-if="selectedAccount?.profile.id === acc.profile.id"
								class="size-5 text-brand shrink-0"
							/>
							<RadioButtonIcon v-else class="size-5 text-secondary shrink-0" />
							<Avatar :src="getAccountAvatarUrl(acc)" size="28px" class="rounded-lg shrink-0" />
							<span
								class="truncate text-sm font-medium"
								:class="selectedAccount?.profile.id === acc.profile.id ? 'text-contrast font-bold' : 'text-primary'"
							>
								{{ acc.profile.name }}
							</span>
							<span
								v-if="acc.access_token === '0'"
								class="text-[10px] px-1.5 py-0.2 rounded bg-surface-5 text-secondary font-medium shrink-0 ml-1"
							>
								Офлайн
							</span>
						</div>
						<IconButton
							type="quiet"
							color="red"
							label="Видалити акаунт"
							class="!size-7 hover:!bg-red/20 hover:!text-red"
							@click.stop="removeAccount(acc.profile.id)"
						>
							<TrashIcon class="size-4" />
						</IconButton>
					</div>
				</div>
			</div>

			<div v-else class="flex flex-col items-center justify-center p-6 rounded-2xl bg-surface-2 border border-dashed border-surface-5 text-center">
				<UserIcon class="size-10 text-secondary mb-2 opacity-60" />
				<span class="text-contrast font-semibold text-sm">Немає доданих акаунтів</span>
				<span class="text-secondary text-xs mt-1">Додайте Microsoft або офлайн акаунт нижче для початку гри</span>
			</div>

			<!-- Add Account Actions -->
			<div class="flex flex-col gap-2 pt-2 border-0 border-t border-solid border-surface-5">
				<span class="text-xs font-semibold text-secondary uppercase tracking-wider px-1">
					Додати новий акаунт
				</span>
				
				<div class="grid grid-cols-2 gap-2">
					<Button
						type="colored"
						color="brand"
						:disabled="loginDisabled"
						class="w-full justify-center"
						@click="loginMicrosoft"
					>
						<SpinnerIcon v-if="loginDisabled" class="animate-spin size-4" />
						<LogInIcon v-else class="size-4" />
						Microsoft акаунт
					</Button>
					<Button
						type="outlined"
						:disabled="loginDisabled"
						class="w-full justify-center"
						@click="showOfflineInput = !showOfflineInput"
					>
						<PlusIcon class="size-4" />
						Піратка (Офлайн)
					</Button>
				</div>

				<!-- Offline Input Drawer -->
				<Transition name="fade">
					<div
						v-if="showOfflineInput"
						class="flex flex-col gap-2 p-3 mt-1 rounded-xl bg-surface-3 border border-solid border-surface-5"
					>
						<span class="text-xs text-secondary font-medium">Введіть нікнейм для гри без ліцензії (скіни автоматично завантажуються з Ely.by):</span>
						<div class="flex gap-2">
							<Input
								v-model="offlineUsername"
								placeholder="Нікнейм гравця"
								wrapper-class="flex-1"
								auto-focus
								@keydown.enter="addOffline(offlineUsername)"
							/>
							<Button
								type="colored"
								color="purple"
								:disabled="!offlineUsername.trim() || loginDisabled"
								@click="addOffline(offlineUsername)"
							>
								Зберегти
							</Button>
						</div>
					</div>
				</Transition>
			</div>
		</div>

		<template #actions>
			<div class="flex justify-end">
				<Button type="outlined" @click="hide">
					<XIcon class="size-4" />
					Закрити
				</Button>
			</div>
		</template>
	</NewModal>
</template>

<style scoped>
.fade-enter-active,
.fade-leave-active {
	transition: opacity 0.2s ease, transform 0.2s ease;
}
.fade-enter-from,
.fade-leave-to {
	opacity: 0;
	transform: translateY(-4px);
}
</style>
